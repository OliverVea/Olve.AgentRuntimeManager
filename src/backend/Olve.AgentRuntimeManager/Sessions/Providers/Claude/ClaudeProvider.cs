using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Conversations;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Skills;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// Runs Claude Code agents: one locked-down <c>claude -p</c> process per session, spoken to in the
/// CLI's own <c>stream-json</c> protocol (OPEN-QUESTIONS A10). The agent sees nothing of the
/// machine's Claude Code setup: no user or project settings, plugins, MCP servers, claude.ai
/// connectors, skills or memory. Only the login is shared. A session that names ARM's tools (M10)
/// gets only those, served by <c>olve-arm-tools</c> as its one MCP server; one with an empty list
/// has no tools at all. Claude Code's built-in tools are never on. Its skills (M10b) are copied
/// into the session's folder by the supervisor before it starts and named to the tools server.
/// Without permission prompts: the VM is the sandbox.
/// </summary>
public sealed class ClaudeProvider(
    IOptions<ClaudeProviderOptions> options, Supervisors supervisors, RepoCheckouts repos, ILogger<ClaudeProvider> logger, IOptions<ToolsOptions>? tools = null,
    IOptions<SkillsOptions>? skills = null)
    : IAgentProvider
{
    public const string ProviderName = "claude";

    /// <summary>What a resumed agent is told (gentle restart: its supervisor was lost mid-turn).</summary>
    public const string ResumePrompt =
        "ARM restarted while you were working. Continue where you left off; if you were in the middle of a tool call, check its effect before repeating it.";

    /// <summary>
    /// Why a session whose supervisor was lost fails instead of resuming: its agent may still run
    /// (e.g. one from before the agent user, docs/AGENT-USER.md), and a second must not join it.
    /// </summary>
    public const string OrphanAlive =
        "ARM restarted and lost this session's supervisor, and its agent may still be running (it could not be killed), so it was not resumed.";

    /// <summary>Environment variables passed through to the agent; everything else is withheld (A5b).</summary>
    public string Name => ProviderName;

    public string? DisplayName => options.Value.DisplayName;

    public IReadOnlyList<ModelOptions> Models => ModelOptions.OrDefaults(options.Value.Models, ClaudeProviderOptions.DefaultModels);

    /// <summary>
    /// Starts Claude Code with the prompt and the messages held for it, or, for a session that
    /// continues (M11), resumes its Claude session with the messages as what it's told next.
    /// </summary>
    public IAgentRun Start(AgentLaunch launch)
    {
        var (session, providerSessionId) = launch.Resume is { } resume
            ? (["--resume", resume], resume)
            : (new[] { "--session-id", launch.ProviderSessionId.ToString() }, launch.ProviderSessionId.ToString());
        var input = launch.Input.Select(text => (Id: Guid.NewGuid(), Text: text)).ToList();
        var agent = Launch(launch.SessionId, launch.RunId, launch.Model, session, input, launch.Env, launch.Repos, launch.Tools, launch.Skills);
        logger.LogInformation("Session {SessionId}: {Started} Claude Code (attempt {Attempt}, run {RunId}, {Messages} messages)",
            launch.SessionId, launch.Resume is null ? "started" : "resumed", launch.Attempt, launch.RunId, launch.Messages?.Count ?? 0);
        // The first is taken up before any turn can end: the run waits for the others.
        return new ClaudeRun(agent, providerSessionId, options.Value.ExitGrace, input.Skip(1).Select(m => m.Id));
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
        if (exit is not null || supervisors.IsListening(recovery.SessionId, recovery.RunId))
        {
            logger.LogInformation("Session {SessionId}: re-attaching to run {RunId}", recovery.SessionId, recovery.RunId);
            var attached = supervisors.Attach(recovery.SessionId, recovery.RunId, folder, recovery.Repos?.Count ?? 0, repos.GitTimeout, repos.CheckoutTimeout);
            return new RecoveredAgent(new ClaudeRun(attached, recovery.ProviderSessionId, settings.ExitGrace), Resumed: false);
        }

        if (SupervisorFiles.ReadInfo(folder) is { } info && info.RunId == recovery.RunId)
        {
            var orphanGone = supervisors.KillOrphan(info);
            var output = Path.Combine(folder, SupervisorFiles.Output);
            if (File.Exists(output) && ClaudeStreamJson.TurnSucceeded(SupervisedAgent.ReadLines(output, info.OutputStart)))
            {
                logger.LogInformation("Session {SessionId}: run {RunId} finished its turn while no server was watching", recovery.SessionId, recovery.RunId);
                return new RecoveredAgent(new EndedRun(recovery.ProviderSessionId, new AgentOutcome.Completed(0)), Resumed: false);
            }

            if (!orphanGone)
            {
                // Resuming would put a second agent on the Claude session.
                logger.LogError("Session {SessionId}: run {RunId}'s agent may still be running and could not be killed; not resuming",
                    recovery.SessionId, recovery.RunId);
                return new RecoveredAgent(new EndedRun(recovery.ProviderSessionId, new AgentOutcome.Failed(OrphanAlive)), Resumed: false);
            }
        }
        else
        {
            // No supervisor.json of the run: its agent never started (the supervisor was lost during
            // the checkout, M5e). It's launched again as it was: a run that resumed a Claude session
            // (one that continued) resumes it with the messages it was given; one that started a
            // session starts it, with its prompt and messages (that session never came to exist).
            logger.LogWarning("Session {SessionId}: run {RunId}'s supervisor is gone and its agent never started; launching it again as run {ResumeRunId}",
                recovery.SessionId, recovery.RunId, recovery.ResumeRunId);
            var (session, told) = recovery.Resume is { } resume
                ? (["--resume", resume], recovery.Messages ?? [])
                : (new[] { "--session-id", recovery.ProviderSessionId }, (IReadOnlyList<string>)[recovery.Prompt, .. recovery.Messages ?? []]);
            var input = told.Select(text => (Id: Guid.NewGuid(), Text: text)).ToList();
            var again = Launch(recovery.SessionId, recovery.ResumeRunId, recovery.Model, session, input, recovery.Env, recovery.Repos, recovery.Tools, recovery.Skills);
            // As at a start: the first is taken up before any turn can end, the run waits for the others.
            return new RecoveredAgent(new ClaudeRun(again, recovery.ProviderSessionId, settings.ExitGrace, input.Skip(1).Select(m => m.Id)), Resumed: true);
        }

        logger.LogWarning("Session {SessionId}: run {RunId}'s supervisor is gone; resuming Claude session {ProviderSessionId} as run {ResumeRunId}",
            recovery.SessionId, recovery.RunId, recovery.ProviderSessionId, recovery.ResumeRunId);
        var resumed = Launch(recovery.SessionId, recovery.ResumeRunId, recovery.Model, ["--resume", recovery.ProviderSessionId], [(Guid.NewGuid(), ResumePrompt)], recovery.Env, recovery.Repos,
            recovery.Tools, recovery.Skills);
        return new RecoveredAgent(new ClaudeRun(resumed, recovery.ProviderSessionId, settings.ExitGrace), Resumed: true);
    }

    /// <summary>Removes a deleted session's worktrees from the shared copies (M5e).</summary>
    public IReadOnlyList<RepoRecord> RemoveRepos(Guid sessionId, IReadOnlyList<RepoRecord> sessionRepos) =>
        repos.Remove(sessionId, Workplace(options.Value, sessionId), sessionRepos);

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

    /// <summary>The session's conversation newest first, read from the end of its raw output only as far as it's enumerated.</summary>
    public IEnumerable<ConversationEntryRecord> ConversationNewestFirst(Guid sessionId) =>
        ClaudeConversation.ReadNewestFirst(OutputLines.Backwards(Path.Combine(Folder(options.Value, sessionId), SupervisorFiles.Output)), ReportUnknown);

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
    /// Starts the agent under a supervisor, in the session's folder, with <paramref name="input"/>
    /// as its first messages (each with its uuid) and the session's <paramref name="env"/> in its environment.
    /// The supervisor checks out <paramref name="sessionRepos"/> in the workplace first (M5e; those
    /// already there are kept); the agent starts in the workplace, as without repositories, each in its own folder there.
    /// Its tools are <paramref name="sessionTools"/> (M10) and nothing else, from ARM's tools server
    /// (none at all for an empty list); every launch of the session passes them (a start, a retry,
    /// a continuation, a resume). Its skills, <paramref name="sessionSkills"/> (M10b), the supervisor
    /// copies from the registry into the session's folder first (those already there are kept), and
    /// the tools server is told each one's copy (only with tools: it's only started then).
    /// </summary>
    private SupervisedAgent Launch(Guid sessionId, Guid runId, string model, string[] session,
        IReadOnlyList<(Guid Id, string Text)> input, IReadOnlyDictionary<string, string>? env, IReadOnlyList<RepoRecord>? sessionRepos,
        IReadOnlyList<string>? sessionTools, IReadOnlyList<string>? sessionSkills = null)
    {
        var settings = options.Value;
        var folder = Directory.CreateDirectory(Folder(settings, sessionId)).FullName;
        // Made by Supervisors.Launch, or with an agent user by the supervisor, as that user (docs/AGENT-USER.md).
        var workDirectory = Workplace(settings, sessionId);
        var sessionSkillCopies = SkillCopies(skills?.Value ?? new SkillsOptions(), folder, sessionSkills);
        // The tools server is the agent's child, so it runs as the agent user too: its journal is in
        // the session's folder, which that user writes.
        var mcpConfig = sessionTools is { Count: > 0 }
            ? McpConfig((tools?.Value ?? new ToolsOptions()).Resolved(), workDirectory, Path.Combine(folder, ToolsLaunch.JournalFileName), sessionTools, sessionSkillCopies)
            : null;
        return supervisors.Launch(sessionId, runId, folder, workDirectory,
            ResolveCommand(settings.Command), Arguments(session, model, mcpConfig), AgentEnvironment(settings, env),
            [.. input.Select(m => ClaudeStreamJson.UserMessage(m.Text, m.Id))],
            repos.Plan(sessionId, workDirectory, sessionRepos), repos.GitTimeout, repos.CheckoutTimeout, sessionSkillCopies);
    }

    /// <summary>
    /// What the supervisor copies for the session's skills (M10b): each from the registry's folder
    /// (<see cref="SkillsOptions.Root"/>) to <c>&lt;session folder&gt;/skills/&lt;name&gt;</c>, each once.
    /// </summary>
    /// <exception cref="InvalidOperationException">A stored name isn't a skill name (a corrupt row): nothing is copied, the start fails with this.</exception>
    public static IReadOnlyList<SupervisorSkill> SkillCopies(SkillsOptions settings, string folder, IReadOnlyList<string>? sessionSkills) =>
        [.. (sessionSkills ?? []).Distinct(StringComparer.Ordinal).Select(name => SkillNames.IsValid(name)
            ? new SupervisorSkill { Name = name, Source = settings.FolderOf(name), Target = Path.Combine(folder, ToolsLaunch.SkillsFolderName, name) }
            : throw new InvalidOperationException($"Copying the session's skills failed: '{name}' isn't a skill name."))];

    /// <summary>
    /// How the agent is launched: the lockdown flags, and <paramref name="session"/>
    /// (<c>--session-id &lt;id&gt;</c> for a new one, <c>--resume &lt;id&gt;</c>): never Claude Code's
    /// built-in tools, and with <paramref name="mcpConfig"/> (<see cref="McpConfig"/>, M10) ARM's
    /// tools (none: no tools at all).
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string[] session, string model, string? mcpConfig = null)
    {
        List<string> arguments =
        [
            "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json",
            // What it's told goes into its output too, so the conversation (M5b) has it.
            "--replay-user-messages",
            // --session-id: ARM's session id on the first attempt, a new one on a retry (Claude keeps
            // the failed attempt's); --resume: the session it continues.
            .. session,
            // Its tools, without asking: the VM is the sandbox.
            "--permission-mode", "bypassPermissions",
            // Nothing of the user's: settings (and with them hooks and plugins), MCP servers, skills.
            "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands",
            // No built-in tool at all, ever: its tools are ARM's (M10).
            "--tools", "",
        ];
        if (mcpConfig is not null)
        {
            // ARM's tools server, the only MCP server.
            arguments.AddRange(["--mcp-config", mcpConfig]);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        return arguments;
    }

    /// <summary>
    /// The MCP configuration that gives the agent ARM's tools (M10), as JSON: one stdio server,
    /// <see cref="ToolsLaunch.ServerName"/>, that the agent starts as <paramref name="command"/> with
    /// the session's workplace, its journal, the tools it named (each once, in order) and, after
    /// them, its skills (M10b) as <c>--skill &lt;name&gt;=&lt;the session's copy&gt;</c>.
    /// </summary>
    internal static string McpConfig(string command, string workplace, string journal, IReadOnlyList<string> sessionTools, IReadOnlyList<SupervisorSkill>? sessionSkills = null)
    {
        var arguments = new JsonArray(ToolsLaunch.Workplace, workplace, ToolsLaunch.Journal, journal);
        foreach (var tool in sessionTools.Distinct(StringComparer.Ordinal))
        {
            arguments.Add(ToolsLaunch.Tool);
            arguments.Add(tool);
        }

        foreach (var skill in sessionSkills ?? [])
        {
            arguments.Add(ToolsLaunch.Skill);
            arguments.Add($"{skill.Name}={skill.Target}");
        }

        var server = new JsonObject { ["type"] = "stdio", ["command"] = command, ["args"] = arguments };
        return new JsonObject { ["mcpServers"] = new JsonObject { [ToolsLaunch.ServerName] = server } }.ToJsonString();
    }

    /// <summary>
    /// The session's own variables (<paramref name="env"/>), then the host's (<see cref="HostEnvironment"/>),
    /// the login token and what keeps Claude Code to itself on top: ARM's always win (their names are
    /// reserved, so they don't meet).
    /// </summary>
    internal static Dictionary<string, string> AgentEnvironment(ClaudeProviderOptions settings, IReadOnlyDictionary<string, string>? env = null)
    {
        var environment = HostEnvironment.With(env);
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is { Length: > 0 } token)
        {
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = token;
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

    /// <summary>The agent's workplace in the session's folder: where its repositories are checked out.</summary>
    private static string Workplace(ClaudeProviderOptions settings, Guid sessionId) => Path.Combine(Path.GetFullPath(Folder(settings, sessionId)), "work");

    private static string WorkRoot(ClaudeProviderOptions settings) =>
        settings.WorkRoot is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "sessions");
}
