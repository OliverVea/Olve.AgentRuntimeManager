using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Activities;

/// <summary>
/// A session's activity (M5f), picked from its conversation: what the agent said (its first line),
/// the tools it called (<c>Bash → npx mise run ci</c>, <see cref="ToolGist"/>), and what failed (a
/// tool result or a turn, and a failed session's error last). Thinking, notices, prompts, results
/// and turn ends that didn't fail, and everything a subagent did, are left out.
/// </summary>
public static class ActivityLines
{
    /// <summary>
    /// The latest <paramref name="limit"/> lines, oldest first, from the conversation newest entry
    /// first: it's enumerated only until they're found.
    /// </summary>
    public static IReadOnlyList<ActivityLine> Pick(SessionRecord session, IEnumerable<ConversationEntryRecord> newestFirst, int limit)
    {
        var picked = new List<ActivityLine>(limit);
        var failure = session is { Status: SessionStatus.Failed, Error: var error }
            ? Line(ActivityKind.Error, ActivityText.ErrorLine(error) ?? "The session failed.")
            : null;
        if (failure is not null)
        {
            picked.Add(failure);
        }

        var latest = true;
        foreach (var entry in picked.Count < limit ? newestFirst : [])
        {
            if (Of(entry) is not { } line)
            {
                continue;
            }

            // A failed session's error is usually its last turn's too: once is enough.
            if (!(latest && line == failure))
            {
                picked.Add(line);
            }

            latest = false;
            if (picked.Count >= limit)
            {
                break;
            }
        }

        picked.Reverse();
        return picked;
    }

    /// <summary>An entry's line, or null if it's left out.</summary>
    public static ActivityLine? Of(ConversationEntryRecord entry) => entry switch
    {
        { ParentToolId: not null } => null,
        { Kind: ConversationEntryKind.Text } => ActivityText.FirstLine(entry.Text) is { } text ? Line(ActivityKind.Text, text) : null,
        { Kind: ConversationEntryKind.ToolCall } => Line(ActivityKind.Tool, ActivityText.Bound(ActivityText.Flatten($"{entry.Tool ?? "tool"} → {ToolGist.Of(entry.Input)}"))),
        { Kind: ConversationEntryKind.ToolResult, IsError: true } => Line(ActivityKind.Error, ActivityText.ErrorLine(entry.Text) ?? "The tool call failed."),
        { Kind: ConversationEntryKind.TurnEnd, IsError: true } => Line(ActivityKind.Error, ActivityText.ErrorLine(entry.Text) ?? "The turn failed."),
        _ => null,
    };

    private static ActivityLine Line(ActivityKind kind, string text) => new() { Kind = kind, Text = text };
}
