using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Settings of ARM's tools (M10; configuration section <c>Tools</c>).</summary>
public sealed class ToolsOptions
{
    public const string Section = "Tools";

    /// <summary>
    /// The tools server (<c>olve-arm-tools</c>) agents start for a session that names tools: a path,
    /// or a name on the agents' <c>PATH</c> (passed as is). Empty: <c>olve-arm-tools</c> next to ARM's own executable.
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// The tools a session gets when it leaves <c>tools</c> out, by name (<see cref="ToolCatalog"/>;
    /// an unknown one fails startup). Empty: such a session gets the provider's built-in tools.
    /// </summary>
    public List<string> Default { get; set; } = [];

    /// <summary>The command to give agents: <see cref="Command"/>, or the default next to ARM's own.</summary>
    public string Resolved() => Command is { Length: > 0 } command ? command : Path.Combine(AppContext.BaseDirectory, "olve-arm-tools");

    /// <summary>
    /// The tools a new session gets (resolved once, when it's created, and stored with it): those it
    /// named (<c>[]</c>: none at all), else <see cref="Default"/>, else null (the provider's built-ins).
    /// </summary>
    public IReadOnlyList<string>? For(IReadOnlyList<string>? requested) =>
        requested ?? (Default is { Count: > 0 } ? [.. Default.Distinct(StringComparer.Ordinal)] : null);

    /// <summary>The names in <see cref="Default"/> ARM has no tool for (checked at startup).</summary>
    public IReadOnlyList<string> UnknownDefaults() => [.. Default.Where(name => ToolCatalog.Find(name) is null).Distinct(StringComparer.Ordinal)];
}
