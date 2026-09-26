using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// The Claude provider against a stub CLI replaying recorded output (Stub/claude-stub.sh), so
/// no test starts the real `claude`.
/// </summary>
public class ClaudeProviderTests
{
    private static readonly string Stub = PrepareStub();

    private readonly string _root = Directory.CreateTempSubdirectory("arm-claude-").FullName;

    [After(Test)]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Turn_Succeeds_Completes()
    {
        var launch = Launch("Say READY");

        var run = Provider().Start(launch);
        var outcome = await run.Completion;

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(run.ProviderSessionId).IsEqualTo(launch.SessionId.ToString());
    }

    [Test]
    public async Task Prompt_IsSentAsAStreamJsonUserMessage()
    {
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion;

        var sent = await File.ReadAllTextAsync(Path.Combine(Folder(launch), "work", "prompt.jsonl"));
        await Assert.That(sent.Trim()).IsEqualTo(ClaudeStreamJson.UserMessage("Say READY"));
    }

    [Test]
    public async Task Output_IsKeptLineByLine()
    {
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion;

        var kept = await File.ReadAllLinesAsync(Path.Combine(Folder(launch), "output.jsonl"));
        var recorded = await File.ReadAllLinesAsync(Path.Combine(Path.GetDirectoryName(Stub)!, "success.jsonl"));
        await Assert.That(kept).IsEquivalentTo(recorded);
    }

    [Test]
    public async Task Agent_IsLockedDown()
    {
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion;

        var (arguments, _) = await Invocation(launch);
        string[] expected =
        [
            "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json",
            "--session-id", launch.SessionId.ToString(),
            "--tools", "", "--permission-prompts", "none",
            "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands",
            "--model", "sonnet",
        ];
        // In order: each flag must be followed by its value.
        await Assert.That(string.Join('\u0001', arguments)).IsEqualTo(string.Join('\u0001', expected));
    }

    [Test]
    public async Task Environment_IsOnlyTheAllowlistAndTheLockdown()
    {
        Environment.SetEnvironmentVariable("ARM_TEST_SECRET", "hunter2");
        var launch = Launch("Say READY");

        await Provider().Start(launch).Completion;

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
    public async Task ConfigDirectory_IsPassedAsClaudeConfigDir()
    {
        var launch = Launch("Say READY");

        await Provider(o => o.ConfigDirectory = "/etc/claude-arm").Start(launch).Completion;

        var (_, environment) = await Invocation(launch);
        await Assert.That(environment["CLAUDE_CONFIG_DIR"]).IsEqualTo("/etc/claude-arm");
    }

    [Test]
    public async Task NoModel_LeavesTheChoiceToClaude()
    {
        var launch = Launch("Say READY") with { Model = " " };

        await Provider().Start(launch).Completion;

        var (arguments, _) = await Invocation(launch);
        await Assert.That(arguments).DoesNotContain("--model");
    }

    [Test]
    public async Task Turn_Errors_Fails()
    {
        var outcome = await Provider().Start(Launch("stub:error")).Completion;

        await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
        await Assert.That(((AgentOutcome.Failed)outcome).Error).Contains("error_during_execution");
    }

    [Test]
    public async Task ApiRejectingTheToken_IsTheProvidersTrouble_Unauthorized()
    {
        var outcome = await Provider().Start(Launch("stub:unauthorized")).Completion;

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Unavailable(
            ProviderTrouble.Unauthorized, "Failed to authenticate. API Error: 401 OAuth access token is invalid."));
    }

    [Test]
    public async Task UsageLimit_IsLimited_UntilItResets()
    {
        var outcome = await Provider().Start(Launch("stub:limited")).Completion;

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Unavailable(
            ProviderTrouble.Limited, "You've hit your session limit · resets 10pm (UTC)", DateTimeOffset.FromUnixTimeSeconds(1790460000)));
    }

    [Test]
    [Arguments("stub:overloaded")]
    [Arguments("stub:server-error")]
    [Arguments("stub:offline")]
    public async Task ApiDownOrUnreachable_IsUnreachable(string prompt)
    {
        var outcome = await Provider().Start(Launch(prompt)).Completion;

        var unavailable = await Assert.That(outcome).IsTypeOf<AgentOutcome.Unavailable>();
        await Assert.That(unavailable!.Trouble).IsEqualTo(ProviderTrouble.Unreachable);
        await Assert.That(unavailable.Error).StartsWith("API Error: ");
    }

    [Test]
    public async Task Retry_UsesTheAttemptsOwnSessionId()
    {
        var launch = Launch("Say READY") with { Attempt = 2, ProviderSessionId = Guid.NewGuid() };

        var run = Provider().Start(launch);
        await run.Completion;

        var (arguments, _) = await Invocation(launch);
        await Assert.That(arguments[Array.IndexOf(arguments, "--session-id") + 1]).IsEqualTo(launch.ProviderSessionId.ToString());
        await Assert.That(run.ProviderSessionId).IsEqualTo(launch.ProviderSessionId.ToString());
    }

    [Test]
    public async Task Exit_BeforeAResult_FailsWithTheExitCodeAndStderr()
    {
        var outcome = await Provider().Start(Launch("stub:crash")).Completion;

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
    public async Task MissingCommand_FailsToStart() =>
        await Assert.That(() => Provider(o => o.Command = Path.Combine(_root, "no-such-claude")).Start(Launch("x")))
            .ThrowsException();

    private ClaudeProvider Provider(Action<ClaudeProviderOptions>? configure = null)
    {
        var options = new ClaudeProviderOptions { Command = Stub, WorkRoot = _root };
        configure?.Invoke(options);
        return new ClaudeProvider(Options.Create(options), NullLogger<ClaudeProvider>.Instance);
    }

    private static AgentLaunch Launch(string prompt)
    {
        var id = Guid.NewGuid();
        return new(id, prompt, "sonnet", 1, id);
    }

    private string Folder(AgentLaunch launch) => Path.Combine(_root, launch.SessionId.ToString());

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
