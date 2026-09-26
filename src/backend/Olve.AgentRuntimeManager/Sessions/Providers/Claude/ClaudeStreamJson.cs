using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// The parts of Claude Code's <c>stream-json</c> protocol ARM speaks: one JSON object per line,
/// user messages in on stdin, events (<c>system</c>, <c>assistant</c>, <c>user</c>, <c>result</c>, …)
/// out on stdout.
/// </summary>
public static class ClaudeStreamJson
{
    /// <summary>A user message, as one stdin line.</summary>
    public static string UserMessage(string text) =>
        new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
            ["parent_tool_use_id"] = null,
        }.ToJsonString();

    /// <summary>The turn's result if <paramref name="line"/> is a <c>result</c> event; anything else (or not JSON) is null.</summary>
    public static ClaudeResult? ParseResult(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject e || Text(e["type"]) != "result")
        {
            return null;
        }

        var errors = e["errors"] is JsonArray array
            ? [.. array.Select(Text).OfType<string>()]
            : Array.Empty<string>();
        var isError = e["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        return new ClaudeResult(Text(e["subtype"]) ?? "unknown", isError, Text(e["result"]), errors);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
