namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>Settings of the <see cref="ClaudeProvider"/> (configuration section <c>Providers:Claude</c>).</summary>
public sealed class ClaudeProviderOptions
{
    public const string Section = "Providers:Claude";

    /// <summary>The models of a server that configures none.</summary>
    public static readonly IReadOnlyList<ModelOptions> DefaultModels =
        [ModelOptions.Of("opus", "Opus"), ModelOptions.Of("sonnet", "Sonnet"), ModelOptions.Of("haiku", "Haiku")];

    /// <summary>Its name for people. Empty: <c>claude</c>.</summary>
    public string? DisplayName { get; set; } = "Claude Code";

    /// <summary>
    /// The models sessions may name, in the order clients show them (aliases or full ids, as Claude
    /// Code's <c>--model</c> takes them). Unset: <see cref="DefaultModels"/>; a configured list replaces them.
    /// </summary>
    public List<ModelOptions>? Models { get; set; }

    /// <summary>The Claude Code executable (a name on <c>PATH</c>, or a path).</summary>
    public string Command { get; set; } = "claude";

    /// <summary>
    /// Where each session gets its folder (<c>&lt;root&gt;/&lt;session id&gt;</c>): the agent's working
    /// directory and its raw output. Empty: <c>olve-arm/sessions</c> in the user's local data folder.
    /// </summary>
    public string WorkRoot { get; set; } = "";

    /// <summary>
    /// Claude Code's own configuration folder (<c>CLAUDE_CONFIG_DIR</c>). Empty: Claude Code's
    /// default (<c>~/.claude</c>), where an interactive login keeps its credentials.
    /// </summary>
    public string ConfigDirectory { get; set; } = "";

    /// <summary>How long an agent may take to exit after its last turn before it's stopped.</summary>
    public TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(10);
}
