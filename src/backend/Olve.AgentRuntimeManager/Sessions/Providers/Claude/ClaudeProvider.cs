using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Conversations;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// Runs Claude Code agents: one locked-down <c>claude -p</c> process per session, spoken to in the
/// CLI's own <c>stream-json</c> protocol (OPEN-QUESTIONS A10). The agent sees nothing of the
/// machine's Claude Code setup: no user or project settings, plugins, MCP servers, claude.ai
/// connectors, skills or memory. Only the login is shared. It has Claude Code's built-in tools,
/// without permission prompts: the VM is the sandbox until ARM's own tools replace them (M5d, M10).
/// </summary>
public sealed class ClaudeProvider(IOptions<ClaudeProviderOptions> options, Supervisors supervisors, ILogger<ClaudeProvider> logger) : IAgentProvider
{
    public const string ProviderName = "claude";

    /// <summary>What a resumed agent is told (gentle restart: its supervisor was lost mid-turn).</summary>
    public const string ResumePrompt =
        "ARM restarted while you were working. Continue where you left off; if you were in the middle of a tool call, check its effect before repeating it.";

    /// <summary>Environment variables passed through to the agent; everything else is withheld (A5b).</summary>
    private static readonly string[] PassedThrough = ["PATH", "HOME", "USER", "LANG", "LC_ALL", "TERM", "TMPDIR", "CLAUDE_CODE_OAUTH_TOKEN"];

    public string Name => ProviderName;

    public IAgentRun Start(AgentLaunch launch)
    {
        var agent = Launch(launch.SessionId, launch.RunId, launch.Model, ["--session-id", launch.ProviderSessionId.ToString()], launch.Prompt, launch.Env);
        logger.LogInformation("Session {SessionId}: started Claude Code (attempt {Attempt}, run {RunId})", launch.SessionId, launch.Attempt, launch.RunId);
        return new ClaudeRun(agent, launch.ProviderSessionId.ToString(), options.Value.ExitGrace);
    }

    /// <summary>
    /// A working session after a restart (docs/GENTLE-RESTART.md): its supervisor answers, or left
    /// the agent's exit → follow the run (from its output on disk); its supervisor is gone → kill
    /// any orphaned agent, then complete if the run's turn succeeded, else resume the Claude session.
    /// </summary>
    public RecoveredAgent? Recover(AgentRecovery recovery)
    {
        var settings = options.Value;
        var folder = Folder(settings, recovery.SessionId);
        var exit = SupervisorFiles.ReadExit(folder) is { } x && x.RunId == recovery.RunId ? x : null;
        if (exit is not null || supervisors.IsListening(recovery.SessionId))
        {
            logger.LogInformation("Session {SessionId}: re-attaching to run {RunId}", recovery.SessionId, recovery.RunId);
            var attached = supervisors.Attach(recovery.SessionId, recovery.RunId, folder);
            return new RecoveredAgent(new ClaudeRun(attached, recovery.ProviderSessionId, settings.ExitGrace), Resumed: false);
        }

        if (SupervisorFiles.ReadInfo(folder) is { } info && info.RunId == recovery.RunId)
        {
            supervisors.KillOrphan(info);
            var output = Path.Combine(folder, SupervisorFiles.Output);
            if (File.Exists(output) && ClaudeStreamJson.TurnSucceeded(SupervisedAgent.ReadLines(output, info.OutputStart)))
            {
                logger.LogInformation("Session {SessionId}: run {RunId} finished its turn while no server was watching", recovery.SessionId, recovery.RunId);
                return new RecoveredAgent(new EndedRun(recovery.ProviderSessionId, new AgentOutcome.Completed(0)), Resumed: false);
            }
        }

        logger.LogWarning("Session {SessionId}: run {RunId}'s supervisor is gone; resuming Claude session {ProviderSessionId} as run {ResumeRunId}",
            recovery.SessionId, recovery.RunId, recovery.ProviderSessionId, recovery.ResumeRunId);
        var resumed = Launch(recovery.SessionId, recovery.ResumeRunId, recovery.Model, ["--resume", recovery.ProviderSessionId], ResumePrompt, recovery.Env);
        return new RecoveredAgent(new ClaudeRun(resumed, recovery.ProviderSessionId, settings.ExitGrace), Resumed: true);
    }

    /// <summary>What Claude Code's output held that the conversation doesn't know, each logged once.</summary>
    private readonly HashSet<string> _unknownSeen = [];

    private void ReportUnknown(string what)
    {
        lock (_unknownSeen)
        {
            if (!_unknownSeen.Add(what))
            {
                return;
            }
        }

        logger.LogWarning("Claude Code's output has an unknown {What}: the conversation leaves it out (a newer Claude Code?)", what);
    }

    /// <summary>The session's conversation, read from its raw output (every attempt and resume, in order).</summary>
    public IReadOnlyList<ConversationEntryRecord> Conversation(Guid sessionId)
    {
        var output = Path.Combine(Folder(options.Value, sessionId), SupervisorFiles.Output);
        return File.Exists(output) ? ClaudeConversation.Read(SupervisedAgent.ReadLines(output, 0), ReportUnknown) : [];
    }

    /// <summary>
    /// Whether Claude Code is logged in, as its agents would be (the same environment and
    /// configuration folder): <c>claude auth status</c>, which reads the credentials without using
    /// them. A token it has but the API would reject still shows only when a session runs.
    /// </summary>
    public async Task<AgentOutcome.Unavailable?> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var info = new ProcessStartInfo(settings.Command)
        {
            WorkingDirectory = Directory.CreateDirectory(WorkRoot(settings)).FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["auth", "status", "--json"])
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (name, value) in AgentEnvironment(settings))
        {
            info.Environment[name] = value;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"'{settings.Command}' did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (ClaudeStreamJson.Parse(await stdout) is { } status && status["loggedIn"] is JsonValue flag && flag.TryGetValue<bool>(out var loggedIn))
            {
                return loggedIn
                    ? null
                    : new AgentOutcome.Unavailable(ProviderTrouble.Unauthorized,
                        "Claude Code is not logged in: no CLAUDE_CODE_OAUTH_TOKEN, and no login in its configuration folder.");
            }

            logger.LogWarning("Claude Code's auth status (exit code {ExitCode}) said nothing about a login", process.ExitCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not ask Claude Code whether it is logged in");
        }

        return null;
    }

    /// <summary>
    /// Starts the agent under a supervisor, in the session's folder, with <paramref name="prompt"/>
    /// as its first message and the session's <paramref name="env"/> in its environment.
    /// </summary>
    private SupervisedAgent Launch(Guid sessionId, Guid runId, string model, string[] session, string prompt, IReadOnlyDictionary<string, string>? env)
    {
        var settings = options.Value;
        var folder = Folder(settings, sessionId);
        var workDirectory = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;
        return supervisors.Launch(sessionId, runId, folder, workDirectory,
            ResolveCommand(settings.Command), Arguments(session, model), AgentEnvironment(settings, env), [ClaudeStreamJson.UserMessage(prompt)]);
    }

    /// <summary>
    /// How the agent is launched: the lockdown flags, and <paramref name="session"/>
    /// (<c>--session-id &lt;id&gt;</c> for a new one, <c>--resume &lt;id&gt;</c>).
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string[] session, string model)
    {
        List<string> arguments =
        [
            "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json",
            // What it's told goes into its output too, so the conversation (M5b) has it.
            "--replay-user-messages",
            // --session-id: ARM's session id on the first attempt, a new one on a retry (Claude keeps
            // the failed attempt's); --resume: the session it continues.
            .. session,
            // Claude Code's built-in tools, without asking: the VM is the sandbox until ARM's own
            // tools replace them (M5d, M10).
            "--permission-mode", "bypassPermissions",
            // Nothing of the user's: settings (and with them hooks and plugins), MCP servers, skills.
            "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands",
        ];
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        return arguments;
    }

    /// <summary>
    /// The session's own variables (<paramref name="env"/>), then the allowlisted ones and what keeps
    /// Claude Code to itself on top: ARM's always win (their names are reserved, so they don't meet).
    /// </summary>
    internal static Dictionary<string, string> AgentEnvironment(ClaudeProviderOptions settings, IReadOnlyDictionary<string, string>? env = null)
    {
        var environment = new Dictionary<string, string>(env ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var name in PassedThrough)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        // The user's claude.ai connectors (Gmail, Calendar, …) load even with --strict-mcp-config.
        environment["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        // The deployment pins the version (vm-deploy.sh); an agent must not update it.
        environment["DISABLE_UPDATES"] = "1";
        if (settings.ConfigDirectory is { Length: > 0 } configDirectory)
        {
            environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        }

        return environment;
    }

    /// <summary>
    /// The command as a full path, looked up on this server's <c>PATH</c> (the supervisor starts it
    /// with the agent's environment, not this one). Throws if there's no such executable.
    /// </summary>
    private static string ResolveCommand(string command)
    {
        if (command.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : throw new FileNotFoundException($"'{command}' does not exist.", command);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"'{command}' is not on the PATH.", command);
    }

    private static string Folder(ClaudeProviderOptions settings, Guid sessionId) => Path.Combine(WorkRoot(settings), sessionId.ToString());

    private static string WorkRoot(ClaudeProviderOptions settings) =>
        settings.WorkRoot is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "sessions");
}
