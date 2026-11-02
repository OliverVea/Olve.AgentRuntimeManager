namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A registered MCP server as the API returns it (M10c).</summary>
public sealed record McpServerBody(string Name, string Description, string Command, string[] Args, Dictionary<string, string> Env, DateTimeOffset UpdatedAt);
