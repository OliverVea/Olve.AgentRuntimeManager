namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// What a call returns to the agent: text, and whether the call failed. A failure is a result the
/// agent reads (MCP's <c>isError</c>), not a JSON-RPC error, so it can correct itself and carry on.
/// </summary>
internal sealed record ToolResult(string Text, bool IsError)
{
    public static ToolResult Ok(string text) => new(text, false);

    public static ToolResult Error(string text) => new(text, true);
}
