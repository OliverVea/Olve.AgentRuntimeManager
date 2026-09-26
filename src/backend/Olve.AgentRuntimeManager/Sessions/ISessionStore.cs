using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// Where sessions are kept (the persistence port, OPEN-QUESTIONS A5). Synchronous: the
/// <see cref="SessionManager"/> writes under its lock, before it changes memory or publishes.
/// </summary>
public interface ISessionStore
{
    void Add(SessionRecord session);

    void Update(SessionRecord session);

    void Delete(Guid id);

    SessionRecord? Get(Guid id);

    /// <summary>Queued and working sessions, oldest first.</summary>
    IReadOnlyList<SessionRecord> Active();

    /// <summary>Sessions matching every given filter, newest first, one page at a time.</summary>
    SessionQueryResult Search(SessionSearch search, int limit, int offset);
}
