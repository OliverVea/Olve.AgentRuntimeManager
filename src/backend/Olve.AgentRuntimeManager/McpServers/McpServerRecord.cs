namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>An external MCP server registered for agents (<c>/api/mcp-servers</c>, M10c).</summary>
public sealed record McpServerRecord
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Command { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required IReadOnlyDictionary<string, string> Env { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
