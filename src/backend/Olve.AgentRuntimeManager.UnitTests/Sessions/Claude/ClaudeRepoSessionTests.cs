using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.UnitTests.Persistence;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// Sessions with repositories (M5e) end to end: the session runtime, the Claude provider on the
/// stub CLI, the real supervisor, local origins. What a kill, a timeout or a message does to a
/// session whose checkout hasn't finished or failed.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class ClaudeRepoSessionTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("arm-repo-sessions-").FullName;
    private readonly TestDatabase _database = new();
    private readonly Supervisors _supervisors;
    private readonly SessionManager _sessions;

    public ClaudeRepoSessionTests()
    {
        _supervisors = new Supervisors(
            Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }), NullLogger<Supervisors>.Instance);
        var repos = new RepoCheckouts(
            Options.Create(new RepoOptions { Root = Path.Combine(_root, "repos"), GitTimeout = TimeSpan.FromSeconds(20) }), _supervisors, NullLogger<RepoCheckouts>.Instance);
        var provider = new ClaudeProvider(
            Options.Create(new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = Path.Combine(_root, "sessions") }),
            _supervisors, repos, NullLogger<ClaudeProvider>.Instance);
        _sessions = new SessionManager(
            new EventBus(TimeProvider.System, Options.Create(new EventOptions())), TimeProvider.System,
            Options.Create(new SessionOptions()), [provider], _database.Store(), NullLogger<SessionManager>.Instance);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _supervisors.Dispose();
        TestProcesses.KillSupervisors(_root);
        _database.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task TimeoutDuringTheCheckout_StopsIt_TheAgentNeverStarts()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var session = Create(GatedSsh.Url(origin), gate, timeoutSeconds: 1);
        await gate.ReachedAsync(Guard);

        var killed = await Eventually(session.Id, s => s.Status == SessionStatus.Killed);

        await Assert.That(killed.KillSource).IsEqualTo(KillSource.Timeout);
        // Promptly: well inside the git timeout (20 s), so a git that just ran into it doesn't pass.
        using (var promptly = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (TestProcesses.WithCommandLine(gate.Fragment).Count > 0 || !Ended(session))
            {
                await Task.Delay(20, promptly.Token);
            }
        }

        await Assert.That(AgentRuns(session)).IsEqualTo(0);
        await Assert.That(Directory.GetFileSystemEntries(Workplace(session))).IsEmpty();
    }

    [Test]
    public async Task AFailedCheckout_ContinuedByAMessage_ChecksOutAgain_AndStartsTheAgentAfresh()
    {
        var path = Path.Combine(_root, "origin", "alpha");
        var session = Create(path);
        var failed = await Eventually(session.Id, s => s.Status == SessionStatus.Failed);
        TestGit.Origin(path);

        var sent = _sessions.Send(session.Id, "try again", "tests");
        var completed = await Eventually(session.Id, s => s.Status == SessionStatus.Completed);

        var worktree = Path.Combine(Workplace(session), "alpha");
        await Assert.That(failed.Error).StartsWith($"Checking out {path} failed: ");
        await Assert.That(sent).IsTypeOf<MessageOutcome.Continued>();
        await Assert.That(completed.Attempts).IsEqualTo(2);
        var arguments = Arguments(worktree);
        await Assert.That(arguments).Contains("--session-id");
        await Assert.That(arguments).DoesNotContain("--resume");
        var told = (await File.ReadAllLinesAsync(Path.Combine(worktree, "input.jsonl"))).Select(l => ClaudeProviderTests.Told(l) ?? "").ToList();
        await Assert.That(told).IsEquivalentTo(["the task", "try again"]);
    }

    [Test]
    public async Task KilledDuringTheCheckout_ThenContinued_RunsOneAgent_Afresh()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var session = Create(GatedSsh.Url(origin), gate);
        await gate.ReachedAsync(Guard);

        _sessions.Kill(session.Id, null, KillSource.User);
        _sessions.Send(session.Id, "go on", "tests");
        gate.Release();
        var completed = await Eventually(session.Id, s => s.Status == SessionStatus.Completed);

        var worktree = Path.Combine(Workplace(session), "alpha");
        await Assert.That(completed.Attempts).IsEqualTo(2);
        await Assert.That(AgentRuns(session)).IsEqualTo(1);
        await Assert.That(Arguments(worktree)).DoesNotContain("--resume");
    }

    private SessionRecord Create(string url, GatedSsh? gate = null, int? timeoutSeconds = null) =>
        _sessions.Create(new CreateSession
        {
            Prompt = "the task", Provider = ClaudeProvider.ProviderName, Model = "sonnet", Caller = "tests", TimeoutSeconds = timeoutSeconds,
            Repos = [new CreateSessionRepo { Url = url }],
        }, gate?.Env) switch
        {
            CreateOutcome.Started started => started.Session,
            var other => throw new InvalidOperationException($"Unexpected {other}."),
        };

    private string Folder(SessionRecord session) => Path.Combine(_root, "sessions", session.Id.ToString());

    private string Workplace(SessionRecord session) => Path.Combine(Folder(session), "work");

    /// <summary>How many agents the session's supervisors started (each says so in its log).</summary>
    private int AgentRuns(SessionRecord session)
    {
        var log = Path.Combine(Folder(session), "supervisor.log");
        return File.Exists(log) ? File.ReadAllLines(log).Count(l => l.Contains(": agent ", StringComparison.Ordinal) && l.EndsWith(" started", StringComparison.Ordinal)) : 0;
    }

    /// <summary>Whether the session's (latest) supervisor has ended: its socket is gone.</summary>
    private bool Ended(SessionRecord session) => !_supervisors.IsListening(session.Id);

    private static string[] Arguments(string directory) =>
        [.. File.ReadAllLines(Path.Combine(directory, "invocation.txt")).Where(l => l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l[4..])];

    private async Task<SessionRecord> Eventually(Guid id, Func<SessionRecord, bool> condition)
    {
        using var timeout = new CancellationTokenSource(Guard);
        while (_sessions.Get(id) is not { } session || !condition(session))
        {
            await Task.Delay(20, timeout.Token);
        }

        return _sessions.Get(id)!;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Guard);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
