using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// A session's conversation, read from Claude Code's <c>stream-json</c> output (<c>output.jsonl</c>):
/// user messages (echoed back with <c>--replay-user-messages</c>) and tool results from <c>user</c>
/// events, the agent's text, thinking and tool calls from <c>assistant</c> events, and each turn's
/// end from <c>result</c> events. Everything else (<c>system</c>, rate limits, …) is left out.
/// </summary>
public static class ClaudeConversation
{
    public static IReadOnlyList<ConversationEntryRecord> Read(IEnumerable<string> lines)
    {
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
                    entries.AddRange(User(e["message"]?["content"], at, parent));
                    break;
                case "assistant":
                    entries.AddRange(Assistant(e["message"]?["content"], at, parent));
                    break;
                case "result" when ClaudeStreamJson.Result(e) is { } result:
                    var text = result.Text is { Length: > 0 } answer ? answer : result.Errors.Count > 0 ? string.Join("; ", result.Errors) : null;
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.TurnEnd, Text: text, IsError: result.IsError, At: at));
                    break;
            }
        }

        return entries;
    }

    /// <summary>A user event: a message to the agent (a prompt), or tool results.</summary>
    private static IEnumerable<ConversationEntryRecord> User(JsonNode? content, DateTimeOffset? at, string? parent)
    {
        if (ClaudeStreamJson.Text(content) is { } message)
        {
            yield return new ConversationEntryRecord(ConversationEntryKind.Prompt, Text: message, ParentToolId: parent, At: at);
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
                        Text: ResultText(block["content"]),
                        ToolId: ClaudeStreamJson.Text(block["tool_use_id"]),
                        IsError: block["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError,
                        ParentToolId: parent,
                        At: at);
                    break;
            }
        }

        if (texts.Count > 0)
        {
            yield return new ConversationEntryRecord(ConversationEntryKind.Prompt, Text: string.Join("\n\n", texts), ParentToolId: parent, At: at);
        }
    }

    /// <summary>An assistant event: its text, thinking and tool calls, in order.</summary>
    private static IEnumerable<ConversationEntryRecord> Assistant(JsonNode? content, DateTimeOffset? at, string? parent)
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
            }
        }
    }

    private static IEnumerable<JsonObject> Blocks(JsonNode? content) =>
        content is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>A tool result's content as text: a string, or its text blocks (other blocks, e.g. images, by type).</summary>
    private static string? ResultText(JsonNode? content) =>
        ClaudeStreamJson.Text(content) ?? (content is JsonArray
            ? string.Join("\n", Blocks(content).Select(b => ClaudeStreamJson.Text(b["text"]) ?? $"[{ClaudeStreamJson.Text(b["type"])}]"))
            : null);

    private static DateTimeOffset? Time(JsonNode? node) =>
        ClaudeStreamJson.Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime()
            : null;
}
