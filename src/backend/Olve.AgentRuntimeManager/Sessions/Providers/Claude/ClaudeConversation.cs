using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// A session's conversation, read from Claude Code's <c>stream-json</c> output (<c>output.jsonl</c>):
/// user messages (echoed back with <c>--replay-user-messages</c>, marked <c>isReplay</c>), Claude
/// Code's own messages to the agent (notices: top-level user text it wasn't sent, e.g. that a
/// background command finished) and tool results from <c>user</c> events, the agent's text, thinking and tool calls from <c>assistant</c> events, and each turn's
/// end from <c>result</c> events. Everything else (<c>system</c>, rate limits, …) is left out.
/// </summary>
public static class ClaudeConversation
{
    /// <summary>Event types that are known and left out on purpose (the rest is reported as unknown).</summary>
    private static readonly HashSet<string> IgnoredEvents = ["system", "rate_limit_event", "stream_event", "tool_progress"];

    /// <param name="unknown">Told what the output held that this doesn't know (e.g. <c>assistant block "server_tool_use"</c>), so a newer Claude Code doesn't go unnoticed.</param>
    public static IReadOnlyList<ConversationEntryRecord> Read(IEnumerable<string> lines, Action<string>? unknown = null)
    {
        unknown ??= _ => { };
        var entries = new List<ConversationEntryRecord>();
        foreach (var line in lines)
        {
            if (ClaudeStreamJson.Parse(line) is not { } e)
            {
                continue;
            }

            var at = Time(e["timestamp"]);
            var parent = ClaudeStreamJson.Text(e["parent_tool_use_id"]);
            switch (ClaudeStreamJson.Text(e["type"]))
            {
                case "user":
                    // What ARM sent comes back as a replay without an origin; Claude Code's own messages
                    // (a background task finishing) are replays too, but carry an `origin`. A
                    // subagent's task comes with its parent.
                    var replayed = e["isReplay"] is JsonValue replay && replay.TryGetValue<bool>(out var isReplay) && isReplay;
                    var told = parent is not null || (replayed && e["origin"] is not JsonObject);
                    entries.AddRange(User(e["message"]?["content"], at, parent, told ? ConversationEntryKind.Prompt : ConversationEntryKind.Notice, unknown));
                    break;
                case "assistant":
                    entries.AddRange(Assistant(e["message"]?["content"], at, parent, unknown));
                    break;
                case "result" when ClaudeStreamJson.Result(e) is { } result:
                    var text = result.Text is { Length: > 0 } answer ? answer : result.Errors.Count > 0 ? string.Join("; ", result.Errors) : null;
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.TurnEnd, Text: text, IsError: result.IsError, At: at));
                    break;
                case { } type when !IgnoredEvents.Contains(type):
                    unknown($"event \"{type}\"");
                    break;
            }
        }

        return entries;
    }

    /// <summary>A user event: a message to the agent (a prompt), or tool results.</summary>
    private static IEnumerable<ConversationEntryRecord> User(JsonNode? content, DateTimeOffset? at, string? parent, ConversationEntryKind message, Action<string> unknown)
    {
        if (ClaudeStreamJson.Text(content) is { } plain)
        {
            yield return new ConversationEntryRecord(message, Text: plain, ParentToolId: parent, At: at);
            yield break;
        }

        var texts = new List<string>();
        foreach (var block in Blocks(content))
        {
            switch (ClaudeStreamJson.Text(block["type"]))
            {
                case "text" when ClaudeStreamJson.Text(block["text"]) is { } text:
                    texts.Add(text);
                    break;
                case "tool_result":
                    yield return new ConversationEntryRecord(
                        ConversationEntryKind.ToolResult,
                        Text: ResultText(block["content"], unknown),
                        ToolId: ClaudeStreamJson.Text(block["tool_use_id"]),
                        IsError: block["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError,
                        ParentToolId: parent,
                        At: at);
                    break;
                case "text":
                    break;
                case var type:
                    unknown($"user block \"{type}\"");
                    break;
            }
        }

        if (texts.Count > 0)
        {
            yield return new ConversationEntryRecord(message, Text: string.Join("\n\n", texts), ParentToolId: parent, At: at);
        }
    }

    /// <summary>An assistant event: its text, thinking and tool calls, in order.</summary>
    private static IEnumerable<ConversationEntryRecord> Assistant(JsonNode? content, DateTimeOffset? at, string? parent, Action<string> unknown)
    {
        foreach (var block in Blocks(content))
        {
            switch (ClaudeStreamJson.Text(block["type"]))
            {
                case "text" when ClaudeStreamJson.Text(block["text"]) is { Length: > 0 } text:
                    yield return new ConversationEntryRecord(ConversationEntryKind.Text, Text: text, ParentToolId: parent, At: at);
                    break;
                // Thinking can come without its text (only a signature): nothing to show then.
                case "thinking" when ClaudeStreamJson.Text(block["thinking"]) is { Length: > 0 } thinking:
                    yield return new ConversationEntryRecord(ConversationEntryKind.Thinking, Text: thinking, ParentToolId: parent, At: at);
                    break;
                case "tool_use":
                    yield return new ConversationEntryRecord(
                        ConversationEntryKind.ToolCall,
                        Tool: ClaudeStreamJson.Text(block["name"]),
                        Input: block["input"] is { } input ? JsonSerializer.SerializeToElement(input) : null,
                        ToolId: ClaudeStreamJson.Text(block["id"]),
                        ParentToolId: parent,
                        At: at);
                    break;
                // Empty text and thinking, and redacted thinking, have nothing to show.
                case "text" or "thinking" or "redacted_thinking":
                    break;
                case var type:
                    unknown($"assistant block \"{type}\"");
                    break;
            }
        }
    }

    private static IEnumerable<JsonObject> Blocks(JsonNode? content) =>
        content is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>A tool result's content as text: a string, or its text blocks (other blocks, e.g. images, by type).</summary>
    private static string? ResultText(JsonNode? content, Action<string> unknown) =>
        ClaudeStreamJson.Text(content) ?? (content is JsonArray
            ? string.Join("\n", Blocks(content).Select(b => BlockText(b, unknown)))
            : null);

    /// <summary>One block of a tool result: its text, a loaded tool by name, anything else (an image) by type.</summary>
    private static string BlockText(JsonObject block, Action<string> unknown)
    {
        var type = ClaudeStreamJson.Text(block["type"]);
        if (ClaudeStreamJson.Text(block["text"]) is { } text)
        {
            return text;
        }

        if (type == "tool_reference" && ClaudeStreamJson.Text(block["tool_name"]) is { } tool)
        {
            return $"Loaded tool: {tool}";
        }

        if (type != "image")
        {
            unknown($"tool result block \"{type}\"");
        }

        return $"[{type}]";
    }

    private static DateTimeOffset? Time(JsonNode? node) =>
        ClaudeStreamJson.Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime()
            : null;
}
