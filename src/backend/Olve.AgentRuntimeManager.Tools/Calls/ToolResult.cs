using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// What a call returns to the agent: text, and whether the call failed. A failure is a result the
/// agent reads (MCP's <c>isError</c>), not a JSON-RPC error, so it can correct itself and carry on.
/// An external MCP server's result (M10c) goes through as it came: <paramref name="Raw"/>, images
/// and all, with <paramref name="Text"/> its JSON.
/// </summary>
internal sealed record ToolResult(string Text, bool IsError, JsonObject? Raw = null)
{
    public static ToolResult Ok(string text) => new(text, false);

    public static ToolResult Error(string text) => new(text, true);

    public static ToolResult Passed(JsonObject raw) =>
        new(raw.ToJsonString(Json), raw["isError"] is JsonValue value && value.TryGetValue<bool>(out var isError) && isError, raw);

    /// <summary>Only what JSON needs escaped: the agent's text stays as it is, and shorter.</summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
