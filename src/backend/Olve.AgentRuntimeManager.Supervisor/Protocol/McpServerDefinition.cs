namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// An external MCP server as a session took it when it was created (M10c): stored with the session,
/// written to <see cref="SupervisorFiles.McpServers"/> by the supervisor, and started by
/// <c>olve-arm-tools</c>.
/// </summary>
public sealed record McpServerDefinition
{
    /// <summary>The name the session has it by, which prefixes its tools (<c>github__create_issue</c>).</summary>
    public required string Name { get; init; }

    public required string Command { get; init; }
    public required IReadOnlyList<string> Args { get; init; }

    /// <summary>Variables it gets on top of the agent's environment.</summary>
    public required IReadOnlyDictionary<string, string> Env { get; init; }
}
