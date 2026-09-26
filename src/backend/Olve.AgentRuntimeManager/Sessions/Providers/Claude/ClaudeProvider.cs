using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// Runs Claude Code agents: one locked-down <c>claude -p</c> process per session, spoken to in the
/// CLI's own <c>stream-json</c> protocol (OPEN-QUESTIONS A10). The agent sees nothing of the
/// machine's Claude Code setup: no built-in tools, no user or project settings, plugins, MCP
/// servers, claude.ai connectors, skills or memory. Only the login is shared.
/// </summary>
public sealed class ClaudeProvider(IOptions<ClaudeProviderOptions> options, ILogger<ClaudeProvider> logger) : IAgentProvider
{
    public const string ProviderName = "claude";

    /// <summary>Environment variables passed through to the agent; everything else is withheld (A5b).</summary>
    private static readonly string[] PassedThrough = ["PATH", "HOME", "USER", "LANG", "LC_ALL", "TERM", "TMPDIR", "CLAUDE_CODE_OAUTH_TOKEN"];

    public string Name => ProviderName;

    public IAgentRun Start(AgentLaunch launch)
    {
        var settings = options.Value;
        var folder = Path.Combine(WorkRoot(settings), launch.SessionId.ToString());
        var workDirectory = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;
        var process = Process.Start(StartInfo(settings, launch, workDirectory))
            ?? throw new InvalidOperationException($"'{settings.Command}' did not start.");
        logger.LogInformation("Session {SessionId}: started Claude Code (attempt {Attempt}, pid {Pid}) in {Folder}", launch.SessionId, launch.Attempt, process.Id, folder);
        return new ClaudeRun(process, launch.ProviderSessionId, launch.Prompt, folder, settings.ExitGrace);
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

        LockDownEnvironment(info, settings);
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

    /// <summary>How the agent is launched: the lockdown flags and a minimal environment.</summary>
    internal static ProcessStartInfo StartInfo(ClaudeProviderOptions settings, AgentLaunch launch, string workDirectory)
    {
        var info = new ProcessStartInfo(settings.Command)
        {
            WorkingDirectory = workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] arguments =
        [
            "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json",
            // ARM's session id on the first attempt; a retry needs a new one (Claude keeps the failed attempt's).
            "--session-id", launch.ProviderSessionId.ToString(),
            // No built-in tools, and anything that would still ask for permission is denied.
            "--tools", "", "--permission-prompts", "none",
            // Nothing of the user's: settings (and with them hooks and plugins), MCP servers, skills.
            "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands",
        ];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(launch.Model))
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(launch.Model);
        }

        LockDownEnvironment(info, settings);
        return info;
    }

    /// <summary>Only the allowlisted variables, plus what keeps Claude Code to itself.</summary>
    private static void LockDownEnvironment(ProcessStartInfo info, ClaudeProviderOptions settings)
    {
        info.Environment.Clear();
        foreach (var name in PassedThrough)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                info.Environment[name] = value;
            }
        }

        // The user's claude.ai connectors (Gmail, Calendar, …) load even with --strict-mcp-config.
        info.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        info.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        // The deployment pins the version (vm-deploy.sh); an agent must not update it.
        info.Environment["DISABLE_UPDATES"] = "1";
        if (settings.ConfigDirectory is { Length: > 0 } configDirectory)
        {
            info.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        }
    }

    private static string WorkRoot(ClaudeProviderOptions settings) =>
        settings.WorkRoot is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "sessions");
}
