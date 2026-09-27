using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Persistence;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// Gentle restart (docs/GENTLE-RESTART.md) with the real supervisor and the stub CLI: a server
/// starts an agent and goes away (<see cref="Supervisors.Dispose"/> lets go without stopping it),
/// and a new server recovers the session.
/// </summary>
public class ClaudeRecoveryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    private readonly string _root = Directory.CreateTempSubdirectory("arm-restart-").FullName;
    private readonly List<Supervisors> _servers = [];

    [After(Test)]
    public void Cleanup()
    {
        foreach (var server in _servers)
        {
            server.Dispose();
        }

        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task AgentStillWorking_IsReattached_AndFollowedToTheEnd()
    {
        var (before, launch) = await StartAsync("stub:gate");
        before.Dispose();

        var recovered = Server().Provider.Recover(Recovery(launch))!;
        Release(launch);

        await Assert.That(recovered.Resumed).IsFalse();
        await Assert.That(await recovered.Run.Completion.WaitAsync(Guard)).IsEqualTo(new AgentOutcome.Completed(0));
    }

    [Test]
    public async Task TurnEndedWhileAway_TheNewServerEndsTheAgent()
    {
        var (before, launch) = await StartAsync("stub:gate");
        before.Dispose();
        Release(launch);
        // The agent has its result out and waits for more input, which only a server can close.
        await Until(() => ClaudeStreamJson.TurnSucceeded(File.ReadAllLines(Path.Combine(Folder(launch), SupervisorFiles.Output))));

        var recovered = Server().Provider.Recover(Recovery(launch))!;

        await Assert.That(await recovered.Run.Completion.WaitAsync(Guard)).IsEqualTo(new AgentOutcome.Completed(0));
    }

    [Test]
    public async Task AgentExitedWhileAway_EndsWithItsOwnOutcome()
    {
        var (before, launch) = await StartAsync("stub:gate-exit stub:error");
        before.Dispose();
        Release(launch);
        await Until(() => SupervisorFiles.ReadExit(Folder(launch)) is not null);
        var server = Server();
        await Until(() => !server.Supervisors.IsListening(launch.SessionId));

        var recovered = server.Provider.Recover(Recovery(launch))!;

        var outcome = await recovered.Run.Completion.WaitAsync(Guard);
        await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
        await Assert.That(((AgentOutcome.Failed)outcome).Error).Contains("error_during_execution");
    }

    [Test]
    public async Task EarlierRunsOfTheSession_AreNotMistakenForTheCurrentOne()
    {
        var first = Server();
        var failed = Launch("stub:error");
        await first.Provider.Start(failed).Completion.WaitAsync(Guard);
        // A retry: the same session and folder, a new run.
        var retry = failed with { Prompt = "stub:gate", Attempt = 2, ProviderSessionId = Guid.NewGuid(), RunId = Guid.NewGuid() };
        _ = first.Provider.Start(retry);
        await Until(() => SupervisorFiles.ReadInfo(Folder(retry))?.RunId == retry.RunId);
        first.Supervisors.Dispose();

        var recovered = Server().Provider.Recover(Recovery(retry))!;
        Release(retry);

        await Assert.That(await recovered.Run.Completion.WaitAsync(Guard)).IsEqualTo(new AgentOutcome.Completed(0));
    }

    [Test]
    public async Task SupervisorLost_OrphanIsKilled_AndTheSessionResumed()
    {
        var (before, launch) = await StartAsync("stub:gate");
        before.Dispose();
        var info = SupervisorFiles.ReadInfo(Folder(launch))!;
        Posix.Kill(info.SupervisorPid, Posix.SigKill);
        var server = Server();
        await Until(() => !server.Supervisors.IsListening(launch.SessionId));

        var recovered = server.Provider.Recover(Recovery(launch))!;

        await Assert.That(recovered.Resumed).IsTrue();
        await Until(() => Posix.StartTime(info.AgentPid) != info.AgentStartTime);
        await Assert.That(await recovered.Run.Completion.WaitAsync(Guard)).IsEqualTo(new AgentOutcome.Completed(0));
        var (arguments, prompt) = Invocation(launch);
        await Assert.That(arguments[Array.IndexOf(arguments, "--resume") + 1]).IsEqualTo(launch.ProviderSessionId.ToString());
        await Assert.That(arguments).DoesNotContain("--session-id");
        await Assert.That(prompt).IsEqualTo(ClaudeStreamJson.UserMessage(ClaudeProvider.ResumePrompt));
        await Assert.That(SupervisorFiles.ReadExit(Folder(launch))!.RunId).IsEqualTo(Recovery(launch).ResumeRunId);
    }

    [Test]
    public async Task SupervisorLost_AfterASuccessfulTurn_Completes_WithoutResuming()
    {
        var (before, launch) = await StartAsync("stub:gate");
        before.Dispose();
        Release(launch);
        await Until(() => ClaudeStreamJson.TurnSucceeded(File.ReadAllLines(Path.Combine(Folder(launch), SupervisorFiles.Output))));
        var info = SupervisorFiles.ReadInfo(Folder(launch))!;
        Posix.Kill(info.SupervisorPid, Posix.SigKill);
        var server = Server();
        await Until(() => !server.Supervisors.IsListening(launch.SessionId));

        var recovered = server.Provider.Recover(Recovery(launch))!;

        await Assert.That(recovered.Resumed).IsFalse();
        await Assert.That(await recovered.Run.Completion.WaitAsync(Guard)).IsEqualTo(new AgentOutcome.Completed(0));
        await Until(() => Posix.StartTime(info.AgentPid) != info.AgentStartTime);
    }

    [Test]
    public async Task ServerRestart_KeepsTheSessionWorking_AndItCompletes()
    {
        using var database = new TestDatabase();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bus = new EventBus(time, Options.Create(new EventOptions()));
        var first = Server();
        var before = Manager(first.Provider);
        var session = ((CreateOutcome.Started)before.Create(new CreateSession
        {
            Prompt = "stub:gate", Provider = ClaudeProvider.ProviderName, Model = "sonnet", Caller = "tests",
        })).Session;
        await Until(() => SupervisorFiles.ReadInfo(Path.Combine(_root, session.Id.ToString())) is not null);
        before.Dispose();
        first.Supervisors.Dispose();

        using var after = Manager(Server().Provider);
        after.Recover();

        await Assert.That(after.Get(session.Id)!.Status).IsEqualTo(SessionStatus.Working);
        await File.WriteAllTextAsync(Path.Combine(_root, session.Id.ToString(), "work", "release"), "");
        await Until(() => after.Get(session.Id)!.Status == SessionStatus.Completed);
        await Assert.That(after.Get(session.Id)!.Attempts).IsEqualTo(1);

        SessionManager Manager(ClaudeProvider provider) => new(
            bus, time, Options.Create(new SessionOptions()), [provider], database.Store(), NullLogger<SessionManager>.Instance);
    }

    /// <summary>A server: its supervisors (sockets under the test's folder) and a Claude provider on the stub.</summary>
    private (Supervisors Supervisors, ClaudeProvider Provider) Server()
    {
        var supervisors = new Supervisors(
            Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }), NullLogger<Supervisors>.Instance);
        _servers.Add(supervisors);
        var options = new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = _root };
        return (supervisors, new ClaudeProvider(Options.Create(options), supervisors, NullLogger<ClaudeProvider>.Instance));
    }

    /// <summary>Starts an agent on a first server, once its supervisor has it running.</summary>
    private async Task<(Supervisors Server, AgentLaunch Launch)> StartAsync(string prompt)
    {
        var (supervisors, provider) = Server();
        var launch = Launch(prompt);
        _ = provider.Start(launch);
        await Until(() => SupervisorFiles.ReadInfo(Folder(launch)) is not null && File.Exists(Path.Combine(Folder(launch), "work", "prompt.jsonl")));
        return (supervisors, launch);
    }

    private static AgentLaunch Launch(string prompt)
    {
        var id = Guid.NewGuid();
        return new(id, prompt, "sonnet", 1, id, Guid.NewGuid());
    }

    /// <summary>The same recovery each time for a launch (so its resume run id is known).</summary>
    private readonly Dictionary<Guid, AgentRecovery> _recoveries = [];

    private AgentRecovery Recovery(AgentLaunch launch)
    {
        if (!_recoveries.TryGetValue(launch.RunId, out var recovery))
        {
            recovery = new AgentRecovery(launch.SessionId, launch.Prompt, launch.Model, launch.ProviderSessionId.ToString(), launch.RunId, Guid.NewGuid());
            _recoveries[launch.RunId] = recovery;
        }

        return recovery;
    }

    private string Folder(AgentLaunch launch) => Path.Combine(_root, launch.SessionId.ToString());

    private void Release(AgentLaunch launch) => File.WriteAllText(Path.Combine(Folder(launch), "work", "release"), "");

    private (string[] Arguments, string Prompt) Invocation(AgentLaunch launch)
    {
        var work = Path.Combine(Folder(launch), "work");
        var arguments = File.ReadAllLines(Path.Combine(work, "invocation.txt"))
            .Where(l => l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l[4..]).ToArray();
        return (arguments, File.ReadAllText(Path.Combine(work, "prompt.jsonl")).Trim());
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
