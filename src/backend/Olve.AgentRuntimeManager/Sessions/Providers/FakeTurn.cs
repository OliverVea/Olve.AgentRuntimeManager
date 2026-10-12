using System.Text.Json;
using System.Text.RegularExpressions;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// What a <see cref="FakeProvider"/> agent's conversation shows, from its prompt's directives, in
/// order (<c>_</c> reads as a space, except in a tool's name):
/// <list type="bullet">
///   <item><c>fake:say=Hello_there</c>: agent text.</item>
///   <item><c>fake:tool=Bash</c>: a tool call and its result; <c>fake:toolerror=Bash</c>: one whose result failed.</item>
///   <item><c>fake:think=…</c>: thinking; <c>fake:notice=…</c>: a notice.</item>
///   <item><c>fake:subagent=…</c>: a <c>Task</c> call whose subagent says this and calls <c>Bash</c>, then its result.</item>
/// </list>
/// </summary>
public static partial class FakeTurn
{
    public static IReadOnlyList<ConversationEntryRecord> Parse(string prompt, DateTimeOffset at)
    {
        var entries = new List<ConversationEntryRecord>();
        foreach (Match match in Step().Matches(prompt))
        {
            var raw = match.Groups["value"].Value;
            var value = raw.Replace('_', ' ');
            var toolId = $"fake-tool-{entries.Count + 1}";
            switch (match.Groups["name"].Value)
            {
                case "say":
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.Text, Text: value, At: at));
                    break;
                case "think":
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.Thinking, Text: value, At: at));
                    break;
                case "notice":
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.Notice, Text: value, At: at));
                    break;
                case "subagent":
                    entries.Add(Call("Task", toolId, at));
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.Text, Text: value, ParentToolId: toolId, At: at));
                    entries.Add(Call("Bash", $"{toolId}-sub", at) with { ParentToolId = toolId });
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.ToolResult, Text: "Bash failed.", ToolId: $"{toolId}-sub", IsError: true, ParentToolId: toolId, At: at));
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.ToolResult, Text: value, ToolId: toolId, IsError: false, At: at));
                    break;
                case var tool:
                    var failed = tool == "toolerror";
                    entries.Add(Call(raw, toolId, at));
                    entries.Add(new ConversationEntryRecord(ConversationEntryKind.ToolResult, Text: failed ? $"{raw} failed." : $"{raw} ran.", ToolId: toolId, IsError: failed, At: at));
                    break;
            }
        }

        return entries;
    }

    private static ConversationEntryRecord Call(string tool, string toolId, DateTimeOffset at) =>
        new(ConversationEntryKind.ToolCall, Tool: tool, Input: JsonSerializer.SerializeToElement(new { fake = true }), ToolId: toolId, At: at);

    [GeneratedRegex(@"\bfake:(?<name>say|toolerror|tool|think|notice|subagent)=(?<value>\S+)")]
    private static partial Regex Step();
}
