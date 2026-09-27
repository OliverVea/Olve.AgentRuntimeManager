using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Conversations;

/// <summary>Domain → contract: entries numbered from 1, and the counts.</summary>
public static class ConversationMapping
{
    public static Conversation ToDto(this IReadOnlyList<ConversationEntryRecord> entries) => new()
    {
        Entries = [.. entries.Select((e, i) => new ConversationEntry
        {
            Seq = i + 1,
            Kind = e.Kind,
            At = e.At,
            Text = e.Text,
            Tool = e.Tool,
            Input = e.Input,
            ToolId = e.ToolId,
            IsError = e.IsError,
            ParentToolId = e.ParentToolId,
        })],
        Turns = entries.Count(e => e.Kind == ConversationEntryKind.TurnEnd),
        Messages = entries.Count(e => e.Kind == ConversationEntryKind.Prompt),
        ToolCalls = entries.Count(e => e.Kind == ConversationEntryKind.ToolCall),
        LastSeq = entries.Count,
    };
}
