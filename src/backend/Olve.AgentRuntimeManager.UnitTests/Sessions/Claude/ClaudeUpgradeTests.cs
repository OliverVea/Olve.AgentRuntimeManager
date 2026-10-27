using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Persistence;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// A server restarting onto sessions it didn't start: a new release's ARM after a deploy, with
/// supervisors of the previous release (listening on <c>&lt;session&gt;.sock</c>, their sessions
/// stored without <c>RunResume</c>/<c>RunMessages</c>), and a session whose timeout ran out while
/// ARM was away. The Claude session of a run whose agent ran is never lost on the way.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class ClaudeUpgradeTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static readonly string Supervisor = Path.Combine(AppContext.BaseDirectory, "olve-arm-supervisor");

    private readonly string _root = Directory.CreateTempSubdirectory("arm-upgrade-").FullName;

    /// <summary>The supervisors this test started: what is its own to kill.</summary>
    private readonly StartedSupervisors _started = new();
    private readonly TestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly EventBus _bus;
    private readonly List<IDisposable> _servers = [];

    public ClaudeUpgradeTests() => _bus = new EventBus(_time, Options.Create(new EventOptions()));

    public void Dispose()
    {
        foreach (var server in Enumerable.Reverse(_servers))
        {
            server.Dispose();
        }

        TestProcesses.KillSupervisors(_root, _started.Pids);
        _database.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task ATimeoutThatRanOutWhileAway_KillsTheSession_ButKeepsItsClaudeSession_AndAMessageResumesIt()
    {
        var before = Server();
        var session = ((CreateOutcome.Started)before.Sessions.Create(new CreateSession
        {
            Prompt = "stub:gate", Provider = ClaudeProvider.ProviderName, Model = "sonnet", Caller = "tests", TimeoutSeconds = 60,
        })).Session;
        await Until(() => SupervisorFiles.ReadInfo(Folder(session.Id)) is not null);
        Stop(before);
        _time.Advance(TimeSpan.FromHours(1));

        var after = Server();
        after.Sessions.Recover();
        var killed = await Eventually(after.Sessions, session.Id, s => s.Status == SessionStatus.Killed);
        after.Sessions.Send(session.Id, "go on", "tests");
        await Eventually(after.Sessions, session.Id, s => s.Status == SessionStatus.Completed);

        await Assert.That(killed.KillSource).IsEqualTo(KillSource.Timeout);
        await Assert.That(killed.ProviderSessionId).IsEqualTo(session.Id.ToString());
        await Assert.That(Arguments(session.Id)).Contains("--resume");
        await Assert.That(Arguments(session.Id)).Contains(session.Id.ToString());
    }

    [Test]
    public async Task ASessionOfThePreviousRelease_KilledRightAfterARestart_KeepsItsClaudeSession_AndResumes()
    {
        var (id, info) = await StartLegacyAsync("stub:gate");

        var after = Server();
        after.Sessions.Recover();
        var killed = ((KillOutcome.Stopped)after.Sessions.Kill(id, null, KillSource.User)).Session;
        await Until(() => Posix.StartTime(info.AgentPid) != info.AgentStartTime);
        after.Sessions.Send(id, "go on", "tests");
        await Eventually(after.Sessions, id, s => s.Status == SessionStatus.Completed);

        await Assert.That(killed.Status).IsEqualTo(SessionStatus.Killed);
        await Assert.That(killed.ProviderSessionId).IsEqualTo(id.ToString());
        await Assert.That(Arguments(id)).Contains("--resume");
        await Assert.That(Arguments(id)).Contains(id.ToString());
    }

    [Test]
    public async Task ASessionOfThePreviousRelease_IsReattached_TakesAMessage_AndContinuesAfterItEnded_OneAgentAtATime()
    {
        var (id, info) = await StartLegacyAsync("stub:gate stub:turns");

        var after = Server();
        after.Sessions.Recover();
        var reattached = after.Sessions.Get(id)!;
        var delivered = after.Sessions.Send(id, "second", "tests");
        await File.WriteAllTextAsync(Path.Combine(Folder(id), "work", "release"), "");
        var completed = await Eventually(after.Sessions, id, s => s.Status == SessionStatus.Completed);
        var told = Told(id);
        var continued = after.Sessions.Send(id, "third", "tests");
        var again = await Eventually(after.Sessions, id, s => s.Status == SessionStatus.Completed && s.RunId != completed.RunId);

        await Assert.That(reattached.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(delivered).IsTypeOf<MessageOutcome.Delivered>();
        await Assert.That(told).Contains("second");
        await Assert.That(continued).IsTypeOf<MessageOutcome.Continued>();
        await Assert.That(again.ProviderSessionId).IsEqualTo(id.ToString());
        await Assert.That(Arguments(id)).Contains("--resume");
        // One agent at a time: the previous release's agent had exited before the continued run's started.
        var log = await File.ReadAllLinesAsync(Path.Combine(Folder(id), SupervisorFiles.Log));
        var exited = Array.FindIndex(log, l => l.Contains($"run {info.RunId}: agent exited", StringComparison.Ordinal));
        var started = Array.FindIndex(log, l => l.Contains($"run {again.RunId}: agent ", StringComparison.Ordinal) && l.EndsWith(" started", StringComparison.Ordinal));
        await Assert.That(exited).IsGreaterThanOrEqualTo(0);
        await Assert.That(started).IsGreaterThan(exited);
        await Assert.That(log.Count(l => l.EndsWith(" started", StringComparison.Ordinal) && l.Contains(": agent ", StringComparison.Ordinal))).IsEqualTo(2);
    }

    /// <summary>
    /// A session as the previous release left it: its supervisor (this build's, which speaks the same
    /// protocol) on the session's socket, <c>&lt;session&gt;.sock</c>, its agent running, its record
    /// working, with no <c>RunResume</c> or <c>RunMessages</c>.
    /// </summary>
    private async Task<(Guid Id, SupervisorInfo Info)> StartLegacyAsync(string prompt)
    {
        var id = Guid.NewGuid();
        var run = Guid.NewGuid();
        var folder = Directory.CreateDirectory(Folder(id)).FullName;
        var launch = new SupervisorLaunch
        {
            Version = SupervisorProtocol.Version,
            RunId = run,
            Command = ClaudeProviderTests.Stub,
            Arguments = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--session-id", id.ToString()],
            Environment = new Dictionary<string, string>
            {
                ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
                ["HOME"] = System.Environment.GetEnvironmentVariable("HOME") ?? _root,
            },
            WorkingDirectory = Path.Combine(folder, "work"),
            Input = [ClaudeStreamJson.UserMessage(prompt, Guid.NewGuid())],
            Folder = folder,
            SocketPath = SupervisorSockets.Legacy(Sockets, id),
            ClientUid = Posix.GetEUid(),
        };
        using (var supervisor = Process.Start(new ProcessStartInfo(Supervisor) { RedirectStandardInput = true, UseShellExecute = false })!)
        {
            await supervisor.StandardInput.WriteAsync(JsonSerializer.Serialize(launch, SupervisorJsonContext.Default.SupervisorLaunch));
            supervisor.StandardInput.Close();
        }

        await Until(() => SupervisorFiles.ReadInfo(folder) is not null && File.Exists(launch.SocketPath));
        var now = _time.GetUtcNow();
        _database.Store().Add(new SessionRecord
        {
            Id = id, Status = SessionStatus.Working, Prompt = prompt, Provider = ClaudeProvider.ProviderName, Model = "sonnet", Caller = "tests",
            CreatedAt = now, StartedAt = now, Attempts = 1, ProviderSessionId = id.ToString(), RunId = run, Tools = [],
        });
        return (id, SupervisorFiles.ReadInfo(folder)!);
    }

    private sealed record ServerParts(Supervisors Supervisors, SessionManager Sessions) : IDisposable
    {
        public void Dispose()
        {
            Sessions.Dispose();
            Supervisors.Dispose();
        }
    }

    /// <summary>A server: supervisors, the Claude provider on the stub CLI, the session runtime on the test's database.</summary>
    private ServerParts Server()
    {
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions { SocketRoot = Sockets }), _started);
        var repos = new RepoCheckouts(Options.Create(new RepoOptions { Root = Path.Combine(_root, "repos") }), supervisors, NullLogger<RepoCheckouts>.Instance);
        var provider = new ClaudeProvider(
            Options.Create(new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = Path.Combine(_root, "sessions") }),
            supervisors, repos, NullLogger<ClaudeProvider>.Instance);
        var sessions = new SessionManager(_bus, _time, Options.Create(new SessionOptions()), [provider], _database.Store(),
            NullLogger<SessionManager>.Instance, _database.Removals());
        var server = new ServerParts(supervisors, sessions);
        _servers.Add(server);
        return server;
    }

    /// <summary>The server stops (as for a deploy): its supervisors and agents keep running.</summary>
    private void Stop(ServerParts server)
    {
        server.Dispose();
        _servers.Remove(server);
    }

    private string Sockets => Path.Combine(_root, "sockets");

    private string Folder(Guid id) => Path.Combine(_root, "sessions", id.ToString());

    /// <summary>The arguments the stub CLI was last started with.</summary>
    private string[] Arguments(Guid id) =>
        [.. File.ReadAllLines(Path.Combine(Folder(id), "work", "invocation.txt")).Where(l => l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l[4..])];

    /// <summary>What the session's agents were told on their stdin, in order.</summary>
    private List<string> Told(Guid id) =>
        [.. File.ReadAllLines(Path.Combine(Folder(id), "work", "input.jsonl")).Select(l => ClaudeProviderTests.Told(l) ?? "")];

    private static async Task<SessionRecord> Eventually(SessionManager sessions, Guid id, Func<SessionRecord, bool> condition)
    {
        using var timeout = new CancellationTokenSource(Guard);
        while (sessions.Get(id) is not { } session || !condition(session))
        {
            await Task.Delay(20, timeout.Token);
        }

        return sessions.Get(id)!;
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
