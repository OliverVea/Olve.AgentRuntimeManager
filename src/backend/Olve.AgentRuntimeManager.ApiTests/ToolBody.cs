namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>One of ARM's tools as <c>GET /api/tools</c> returns it.</summary>
public sealed record ToolBody(string Name, string Description, bool Default);
