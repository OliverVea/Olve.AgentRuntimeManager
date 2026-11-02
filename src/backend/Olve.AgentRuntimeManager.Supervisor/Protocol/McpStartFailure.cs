namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// Why <c>olve-arm-tools</c> couldn't give the agent a session's MCP servers (M10c): written to
/// <see cref="SupervisorFiles.McpFailure"/> before it exits, since the provider only says that it failed.
/// </summary>
public sealed record McpStartFailure
{
    /// <summary>What ARM fails the session with: which server or tool, and what went wrong.</summary>
    public required string Error { get; init; }
}
