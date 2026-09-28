using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// The Claude provider against a stub CLI replaying recorded output (Stub/claude-stub.sh), so
/// no test starts the real `claude`.
/// </summary>
public class ClaudeProviderTests
{
    internal static readonly string Stub = PrepareStub();

    private readonly string _root = Directory.CreateTempSubdirectory("arm-claude-").FullName;

    /// <summary>How long any wait in a test may take: nothing may hang the test run.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [After(Test)]
    public void Cleanup()
    {
        TestProcesses.KillSupervisors(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task Turn_Succeeds_Completes()
    {
        var launch = Launch("Say READY");

        var run = Provider().Start(launch);
        var outcome = await run.Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(run.ProviderSessionId).IsEqualTo(launch.SessionId.ToString());
    }

    [Test]
    public async Task Prompt_IsSentAsAStreamJsonUserMessage()
    {
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var sent = await File.ReadAllTextAsync(Path.Combine(Folder(launch), "work", "prompt.jsonl"));
        await Assert.That(Told(sent.Trim())).IsEqualTo("Say READY");
    }

    [Test]
    public async Task Output_IsKeptLineByLine()
    {
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var kept = await File.ReadAllLinesAsync(Path.Combine(Folder(launch), "output.jsonl"));
        var recorded = await File.ReadAllLinesAsync(Path.Combine(Path.GetDirectoryName(Stub)!, "success.jsonl"));
        await Assert.That(kept).IsEquivalentTo(recorded);
    }

    [Test]
    public async Task Agent_IsLockedDown()
    {
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var (arguments, _) = await Invocation(launch);
        string[] expected =
        [
            "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json",
            "--replay-user-messages",
            "--session-id", launch.SessionId.ToString(),
            "--permission-mode", "bypassPermissions",
            "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands",
            "--model", "sonnet",
        ];
        // In order: each flag must be followed by its value.
        await Assert.That(string.Join('\u0001', arguments)).IsEqualTo(string.Join('\u0001', expected));
    }

    [Test]
    public async Task Environment_IsOnlyTheAllowlistAndTheLockdown()
    {
        // Process-wide, so put back after: no other test sees it.
        Environment.SetEnvironmentVariable("ARM_TEST_SECRET", "hunter2");
        var launch = Launch("Say READY");
        try
        {
            await Provider().Start(launch).Completion.WaitAsync(Guard);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARM_TEST_SECRET", null);
        }

        var (_, environment) = await Invocation(launch);
        await Assert.That(environment["ENABLE_CLAUDEAI_MCP_SERVERS"]).IsEqualTo("false");
        await Assert.That(environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"]).IsEqualTo("1");
        await Assert.That(environment["DISABLE_UPDATES"]).IsEqualTo("1");
        // What bash adds by itself, plus what ARM passes on.
        string[] allowed =
        [
            "PWD", "SHLVL", "_", "OLDPWD",
            "PATH", "HOME", "USER", "LANG", "LC_ALL", "TERM", "TMPDIR", "CLAUDE_CODE_OAUTH_TOKEN",
            "ENABLE_CLAUDEAI_MCP_SERVERS", "CLAUDE_CODE_DISABLE_AUTO_MEMORY", "DISABLE_UPDATES",
        ];
        await Assert.That(environment.Keys.Except(allowed)).IsEmpty();
    }

    [Test]
    public async Task SessionEnv_ReachesTheAgent()
    {
        var launch = Launch("Say READY") with { Env = new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "Oliver" } };

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var (_, environment) = await Invocation(launch);
        await Assert.That(environment["GIT_AUTHOR_NAME"]).IsEqualTo("Oliver");
    }

    [Test]
    public async Task ArmsOwnVariables_WinOverTheSessionsEnv()
    {
        var launch = Launch("Say READY") with
        {
            Env = new Dictionary<string, string> { ["DISABLE_UPDATES"] = "0", ["ENABLE_CLAUDEAI_MCP_SERVERS"] = "true" },
        };

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var (_, environment) = await Invocation(launch);
        await Assert.That(environment["DISABLE_UPDATES"]).IsEqualTo("1");
        await Assert.That(environment["ENABLE_CLAUDEAI_MCP_SERVERS"]).IsEqualTo("false");
    }

    [Test]
    public async Task ConfigDirectory_IsPassedAsClaudeConfigDir()
    {
        var launch = Launch("Say READY");

        await Provider(o => o.ConfigDirectory = "/etc/claude-arm").Start(launch).Completion.WaitAsync(Guard);

        var (_, environment) = await Invocation(launch);
        await Assert.That(environment["CLAUDE_CONFIG_DIR"]).IsEqualTo("/etc/claude-arm");
    }

    [Test]
    public async Task NoModel_LeavesTheChoiceToClaude()
    {
        var launch = Launch("Say READY") with { Model = " " };

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var (arguments, _) = await Invocation(launch);
        await Assert.That(arguments).DoesNotContain("--model");
    }

    [Test]
    public async Task Turn_Errors_Fails()
    {
        var outcome = await Provider().Start(Launch("stub:error")).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
        await Assert.That(((AgentOutcome.Failed)outcome).Error).Contains("error_during_execution");
    }

    [Test]
    public async Task ApiRejectingTheToken_IsTheProvidersTrouble_Unauthorized()
    {
        var outcome = await Provider().Start(Launch("stub:unauthorized")).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Unavailable(
            ProviderTrouble.Unauthorized, "Failed to authenticate. API Error: 401 OAuth access token is invalid."));
    }

    [Test]
    public async Task NoLogin_IsUnauthorized_ThoughNoRequestWasSent()
    {
        var outcome = await Provider().Start(Launch("stub:not-logged-in")).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Unavailable(
            ProviderTrouble.Unauthorized, "Not logged in · Please run /login"));
    }

    [Test]
    public async Task UsageLimit_IsLimited_UntilItResets()
    {
        var outcome = await Provider().Start(Launch("stub:limited")).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Unavailable(
            ProviderTrouble.Limited, "You've hit your session limit · resets 10pm (UTC)", DateTimeOffset.FromUnixTimeSeconds(1790460000)));
    }

    [Test]
    [Arguments("stub:overloaded")]
    [Arguments("stub:server-error")]
    [Arguments("stub:offline")]
    public async Task ApiDownOrUnreachable_IsUnreachable(string prompt)
    {
        var outcome = await Provider().Start(Launch(prompt)).Completion.WaitAsync(Guard);

        var unavailable = await Assert.That(outcome).IsTypeOf<AgentOutcome.Unavailable>();
        await Assert.That(unavailable!.Trouble).IsEqualTo(ProviderTrouble.Unreachable);
        await Assert.That(unavailable.Error).StartsWith("API Error: ");
    }

    [Test]
    public async Task Retry_UsesTheAttemptsOwnSessionId()
    {
        var launch = Launch("Say READY") with { Attempt = 2, ProviderSessionId = Guid.NewGuid() };

        var run = Provider().Start(launch);
        await run.Completion.WaitAsync(Guard);

        var (arguments, _) = await Invocation(launch);
        await Assert.That(arguments[Array.IndexOf(arguments, "--session-id") + 1]).IsEqualTo(launch.ProviderSessionId.ToString());
        await Assert.That(run.ProviderSessionId).IsEqualTo(launch.ProviderSessionId.ToString());
    }

    [Test]
    public async Task Exit_BeforeAResult_FailsWithTheExitCodeAndStderr()
    {
        var outcome = await Provider().Start(Launch("stub:crash")).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed(
            "Claude Code exited with code 3 before finishing its turn: Invalid API key · Please run /login"));
    }

    [Test]
    public async Task Kill_StopsTheAgent()
    {
        var run = Provider().Start(Launch("stub:hang"));
        await Task.Delay(200);

        run.Kill();

        await Assert.That(await run.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsTypeOf<AgentOutcome.Killed>();
    }

    [Test]
    public async Task Agent_ThatLingersAfterItsTurn_IsStoppedAndStillCompletes()
    {
        var run = Provider(o => o.ExitGrace = TimeSpan.FromMilliseconds(200)).Start(Launch("stub:linger"));

        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
    }

    [Test]
    public async Task HeldMessages_FollowThePrompt_AndTheAgentRunsUntilItsLastTurn()
    {
        // The second turn takes longer than the grace an agent gets after its last turn.
        var launch = Launch("stub:turns stub:slow") with { Messages = ["second"] };
        var run = Provider(o => o.ExitGrace = TimeSpan.FromMilliseconds(200)).Start(launch);

        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(Input(launch)).IsEquivalentTo(["stub:turns stub:slow", "second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Results(launch)).IsEqualTo(2);
        await Assert.That(SupervisorFiles.ReadExit(Folder(launch))!.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Message_WhileWorking_IsDelivered_AndGetsItsOwnTurn()
    {
        var launch = Launch("stub:gate stub:turns");
        var run = Provider().Start(launch);

        var sent = run.TrySend("and another thing");
        await File.WriteAllTextAsync(Path.Combine(Folder(launch), "work", "release"), "");
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(sent).IsTrue();
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(Input(launch)).IsEquivalentTo(["stub:gate stub:turns", "and another thing"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Results(launch)).IsEqualTo(2);
        await Assert.That(run.TrySend("too late")).IsFalse();
    }

    [Test]
    public async Task Message_AfterTheLastTurn_IsRefused()
    {
        // Its turn ends, its input is closed, and it lingers: a message can't reach it any more.
        var launch = Launch("stub:linger");
        var run = Provider().Start(launch);
        await Until(() => File.Exists(Path.Combine(Folder(launch), "work", "input-closed")));

        await Assert.That(run.TrySend("late")).IsFalse();

        run.Kill();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Continuing_ResumesTheClaudeSession_WithTheMessagesAsItsInput()
    {
        var launch = Launch("the original prompt") with { Attempt = 2, Resume = "the-claude-session", Messages = ["stub:turns first", "second"] };

        var run = Provider().Start(launch);
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(run.ProviderSessionId).IsEqualTo("the-claude-session");
        var (arguments, _) = await Invocation(launch);
        await Assert.That(arguments[Array.IndexOf(arguments, "--resume") + 1]).IsEqualTo("the-claude-session");
        await Assert.That(arguments).DoesNotContain("--session-id");
        await Assert.That(Input(launch)).IsEquivalentTo(["stub:turns first", "second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Check_LoggedIn_IsFine()
    {
        var config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(config, ".credentials.json"), "{}");

        var trouble = await Provider(o => o.ConfigDirectory = config).CheckAsync(CancellationToken.None);

        await Assert.That(trouble).IsNull();
    }

    [Test]
    public async Task Check_NotLoggedIn_IsUnauthorized()
    {
        Skip.When(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is { Length: > 0 }, "A token in the test's environment logs the stub in.");
        var config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;

        var trouble = await Provider(o => o.ConfigDirectory = config).CheckAsync(CancellationToken.None);

        await Assert.That(trouble!.Trouble).IsEqualTo(ProviderTrouble.Unauthorized);
        await Assert.That(trouble.Error).StartsWith("Claude Code is not logged in");
    }

    [Test]
    public async Task Check_WithoutClaudeCode_CantTell() =>
        await Assert.That(await Provider(o => o.Command = Path.Combine(_root, "no-such-claude")).CheckAsync(CancellationToken.None)).IsNull();

    [Test]
    public async Task MissingCommand_FailsToStart() =>
        await Assert.That(() => Provider(o => o.Command = Path.Combine(_root, "no-such-claude")).Start(Launch("x")))
            .ThrowsException();

    /// <summary>What a stdin line (a stream-json user message) told the agent.</summary>
    internal static string? Told(string line) =>
        JsonNode.Parse(line) is { } message && message["type"]?.GetValue<string>() == "user" ? message["message"]?["content"]?.GetValue<string>() : null;

    private ClaudeProvider Provider(Action<ClaudeProviderOptions>? configure = null)
    {
        var options = new ClaudeProviderOptions { Command = Stub, WorkRoot = _root };
        configure?.Invoke(options);
        return new ClaudeProvider(Options.Create(options), Supervisors(), NullLogger<ClaudeProvider>.Instance);
    }

    /// <summary>The real supervisor (built next to the tests), with its sockets under the test's folder.</summary>
    private Supervisors Supervisors() =>
        new(Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }), NullLogger<Supervisors>.Instance);

    private static AgentLaunch Launch(string prompt)
    {
        var id = Guid.NewGuid();
        return new(id, prompt, "sonnet", 1, id, Guid.NewGuid());
    }

    private string Folder(AgentLaunch launch) => Path.Combine(_root, launch.SessionId.ToString());

    /// <summary>What the agent was told on its stdin, in order.</summary>
    private string[] Input(AgentLaunch launch) =>
        [.. File.ReadAllLines(Path.Combine(Folder(launch), "work", "input.jsonl")).Select(l => Told(l) ?? "")];

    /// <summary>How many turns the agent ended (<c>result</c> events in its output).</summary>
    private int Results(AgentLaunch launch) =>
        File.ReadAllLines(Path.Combine(Folder(launch), "output.jsonl")).Count(l => ClaudeStreamJson.ParseResult(l) is not null);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>What the stub was started with: its arguments, and its environment.</summary>
    private async Task<(string[] Arguments, Dictionary<string, string> Environment)> Invocation(AgentLaunch launch)
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(Folder(launch), "work", "invocation.txt"));
        var arguments = lines.Where(l => l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l[4..]).ToArray();
        var environment = lines.Where(l => l.StartsWith("env:", StringComparison.Ordinal))
            .Select(l => l[4..].Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);
        return (arguments, environment);
    }

    /// <summary>The stub in the test output; copying may drop its execute bit.</summary>
    private static string PrepareStub()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Claude", "Stub", "claude-stub.sh");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }

        return path;
    }
}
