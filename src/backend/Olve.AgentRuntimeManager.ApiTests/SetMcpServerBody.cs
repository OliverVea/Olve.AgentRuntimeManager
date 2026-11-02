namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>The body of <c>PUT /api/mcp-servers/{name}</c> (M10c).</summary>
public sealed record SetMcpServerBody(string Description, string Command, string[]? Args = null, Dictionary<string, string>? Env = null);
