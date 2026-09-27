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
    /// <summary>
    /// A user message, as one stdin line. Its <paramref name="uuid"/> comes back on its replay
    /// (<c>--replay-user-messages</c>) once the agent has taken it up: see <see cref="IsReplay"/>.
    /// </summary>
    public static string UserMessage(string text, Guid? uuid = null)
    {
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
            ["parent_tool_use_id"] = null,
        };
        if (uuid is { } id)
        {
            message["uuid"] = id.ToString();
        }

        return message.ToJsonString();
    }

    /// <summary>
    /// Whether <paramref name="e"/> replays a user message the agent was given (<c>isReplay</c>): it
    /// has taken it up, and a turn with it is under way. <paramref name="uuid"/>: the message's own, if it had one.
    /// </summary>
    public static bool IsReplay(JsonObject e, out Guid? uuid)
    {
        uuid = null;
        if (Text(e["type"]) != "user" || e["isReplay"] is not JsonValue flag || !flag.TryGetValue<bool>(out var replay) || !replay)
        {
            return false;
        }

        uuid = Guid.TryParse(Text(e["uuid"]), out var id) ? id : null;
        return true;
    }

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

    /// <summary>
    /// Whether a run's output (its lines of <c>output.jsonl</c>) ends with a successful turn, with
    /// no message taken up after it: its work is done, even if no exit was recorded.
    /// </summary>
    public static bool TurnSucceeded(IEnumerable<string> lines)
    {
        ClaudeResult? last = null;
        foreach (var e in lines.Select(Parse).OfType<JsonObject>())
        {
            if (IsReplay(e, out _))
            {
                // A turn under way: not done until its own result.
                last = null;
            }
            else if (Result(e) is { } result)
            {
                last = result;
            }
        }

        return last is { IsError: false };
    }

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
    /// The kind of API error (<c>authentication_failed</c>, <c>rate_limit</c>, <c>server_error</c>, …)
    /// if <paramref name="e"/> is the assistant message Claude Code reports one with; else null.
    /// </summary>
    public static string? ApiError(JsonObject e) =>
        Text(e["type"]) == "assistant" ? Text(e["error"]) : null;

    /// <summary>
    /// Whether <paramref name="e"/> is a message the model wrote. Claude Code reports API errors as
    /// assistant messages too, but from model <c>&lt;synthetic&gt;</c>.
    /// </summary>
    public static bool IsModelMessage(JsonObject e) =>
        Text(e["type"]) == "assistant" && e["message"] is JsonObject message && Text(message["model"]) is { } model && model != "<synthetic>";

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
