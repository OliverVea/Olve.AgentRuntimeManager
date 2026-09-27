using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>The fake provider's conversations, per session, in memory (like its agents, gone after a restart).</summary>
internal sealed class FakeConversations
{
    private readonly Dictionary<Guid, List<ConversationEntryRecord>> _entries = [];

    public void Add(Guid sessionId, IEnumerable<ConversationEntryRecord> entries)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(sessionId, out var list))
            {
                _entries[sessionId] = list = [];
            }

            list.AddRange(entries);
        }
    }

    public IReadOnlyList<ConversationEntryRecord> Of(Guid sessionId)
    {
        lock (_entries)
        {
            return _entries.TryGetValue(sessionId, out var list) ? [.. list] : [];
        }
    }
}
