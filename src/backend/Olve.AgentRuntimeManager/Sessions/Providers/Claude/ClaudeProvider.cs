using System.Diagnostics;
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

        return info;
    }

    private static string WorkRoot(ClaudeProviderOptions settings) =>
        settings.WorkRoot is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "sessions");
}
