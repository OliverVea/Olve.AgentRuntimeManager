using System.Text.Json;
using System.Text.RegularExpressions;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// What a <see cref="FakeProvider"/> agent's conversation shows, from its prompt's directives, in
/// order: <c>fake:say=Hello_there</c> is agent text (<c>_</c> reads as a space), <c>fake:tool=Bash</c>
/// a tool call and its result.
/// </summary>
public static partial class FakeTurn
{
    public static IReadOnlyList<ConversationEntryRecord> Parse(string prompt, DateTimeOffset at)
    {
        var entries = new List<ConversationEntryRecord>();
        foreach (Match match in Step().Matches(prompt))
        {
            var value = match.Groups["value"].Value;
            if (match.Groups["name"].Value == "say")
            {
                entries.Add(new ConversationEntryRecord(ConversationEntryKind.Text, Text: value.Replace('_', ' '), At: at));
                continue;
            }

            var toolId = $"fake-tool-{entries.Count + 1}";
            entries.Add(new ConversationEntryRecord(ConversationEntryKind.ToolCall, Tool: value, Input: JsonSerializer.SerializeToElement(new { fake = true }), ToolId: toolId, At: at));
            entries.Add(new ConversationEntryRecord(ConversationEntryKind.ToolResult, Text: $"{value} ran.", ToolId: toolId, IsError: false, At: at));
        }

        return entries;
    }

    [GeneratedRegex(@"\bfake:(?<name>say|tool)=(?<value>\S+)")]
    private static partial Regex Step();
}
