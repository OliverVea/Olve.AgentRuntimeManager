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

    /// <summary>The command to give agents: <see cref="Command"/>, or the default next to ARM's own.</summary>
    public string Resolved() => Command is { Length: > 0 } command ? command : Path.Combine(AppContext.BaseDirectory, "olve-arm-tools");
}
