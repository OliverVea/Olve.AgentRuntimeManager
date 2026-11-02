namespace Olve.AgentRuntimeManager.Tools.External;

/// <summary>What went wrong with an external MCP server, in words for the session's error or the agent.</summary>
internal sealed class ExternalServerFailure(string message) : Exception(message);
