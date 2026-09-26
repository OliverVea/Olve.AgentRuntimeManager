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

    /// <summary>One output line as an event object; null for anything else (or not JSON).</summary>
    public static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The turn's result if <paramref name="line"/> is a <c>result</c> event; anything else (or not JSON) is null.</summary>
    public static ClaudeResult? ParseResult(string line) => Parse(line) is { } e ? Result(e) : null;

    /// <summary>The turn's result if <paramref name="e"/> is a <c>result</c> event, else null.</summary>
    public static ClaudeResult? Result(JsonObject e)
    {
        if (Text(e["type"]) != "result")
        {
            return null;
        }

        var errors = e["errors"] is JsonArray array
            ? [.. array.Select(Text).OfType<string>()]
            : Array.Empty<string>();
        var isError = e["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        return new ClaudeResult(
            Text(e["subtype"]) ?? "unknown", isError, Text(e["result"]), errors,
            Text(e["terminal_reason"]), Number(e["api_error_status"]));
    }

    /// <summary>
    /// Whether <paramref name="e"/> is a <c>rate_limit_event</c> saying the usage limit was hit
    /// (<c>status: rejected</c>), and if so when it resets (<c>resetsAt</c>, Unix seconds; null if not given).
    /// </summary>
    public static bool IsLimitReached(JsonObject e, out DateTimeOffset? resetsAt)
    {
        resetsAt = null;
        if (Text(e["type"]) != "rate_limit_event" || e["rate_limit_info"] is not JsonObject info || Text(info["status"]) != "rejected")
        {
            return false;
        }

        if (info["resetsAt"] is JsonValue reset && reset.TryGetValue<long>(out var seconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="e"/> is a message the model wrote. Claude Code reports API errors as
    /// assistant messages too, but from model <c>&lt;synthetic&gt;</c>.
    /// </summary>
    public static bool IsModelMessage(JsonObject e) =>
        Text(e["type"]) == "assistant" && e["message"] is JsonObject message && Text(message["model"]) is { } model && model != "<synthetic>";

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
