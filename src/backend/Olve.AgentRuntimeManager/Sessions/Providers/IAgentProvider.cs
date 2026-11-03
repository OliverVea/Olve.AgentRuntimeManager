using Olve.AgentRuntimeManager.Sessions.Conversations;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// Runs agents for one provider (<c>fake</c>, later <c>claude</c>, <c>codex</c>): the seam between
/// the session runtime and whatever actually executes an agent (OPEN-QUESTIONS A6).
/// </summary>
public interface IAgentProvider
{
    /// <summary>The name sessions select it by (<c>provider</c>).</summary>
    string Name { get; }

    /// <summary>Its name for people (<c>Claude Code</c>), as configured; blank: <see cref="Name"/>.</summary>
    string? DisplayName => null;

    /// <summary>The models sessions may name for it, in order, as configured on this server; any other is refused.</summary>
    IReadOnlyList<ModelOptions> Models { get; }

    /// <summary>
    /// Starts an agent and returns at once; the run completes on its own. Throws when the agent
    /// can't start (the session then fails). Must not block: it's called while the runtime holds
    /// its lock.
    /// </summary>
    IAgentRun Start(AgentLaunch launch);

    /// <summary>
    /// Whether the provider can run agents at all, found out without running one (no usage): the
    /// trouble if it can't, null if it can or can't tell. Run once at startup.
    /// </summary>
    Task<AgentOutcome.Unavailable?> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult<AgentOutcome.Unavailable?>(null);

    /// <summary>
    /// Finds a working session's agent after a restart (gentle restart, M5a): re-attaches to it,
    /// reports how it ended while no server was watching, or resumes it. Null when it's lost (the
    /// session is then killed). Called once per session at startup, not under the runtime's lock.
    /// </summary>
    RecoveredAgent? Recover(AgentRecovery recovery) => null;

    /// <summary>
    /// A session's conversation so far, in order (M5b): every attempt and resume of its agent.
    /// Entries never change or move, so their positions are stable. Empty when there's none.
    /// </summary>
    IReadOnlyList<ConversationEntryRecord> Conversation(Guid sessionId) => [];

    /// <summary>
    /// The same conversation newest entry first, for readers that need only its end (a session's
    /// activity, M5f): a provider that keeps it in a file reads it from the end, only as far as
    /// it's enumerated.
    /// </summary>
    IEnumerable<ConversationEntryRecord> ConversationNewestFirst(Guid sessionId) => Conversation(sessionId).Reverse();

    /// <summary>
    /// A session that named <paramref name="repos"/> was deleted (M5e): remove its worktrees from the
    /// shared copies (their branches stay), each bounded; never for a session still running. The
    /// repositories whose worktrees are still there (to be tried again later). The default has no
    /// workplace, so nothing to remove.
    /// </summary>
    IReadOnlyList<RepoRecord> RemoveRepos(Guid sessionId, IReadOnlyList<RepoRecord> repos) => [];

    /// <summary>
    /// A session was deleted and its worktrees are gone (#22): take what's left of it on disk out
    /// of use, its folder whole (outputs, skill copies, the supervisor's files), quickly (it's in the
    /// delete request); <see cref="RemoveLeftovers"/> then removes it. Never for a session still
    /// running, nor one whose worktrees are still there. Null when that's done (or there was no
    /// folder), else why not: the folder then stays. The default keeps nothing on disk.
    /// </summary>
    string? RemoveFolder(Guid sessionId) => null;

    /// <summary>
    /// Removes deleted sessions' folders taken out of use (<see cref="RemoveFolder"/>), retrying
    /// what couldn't be removed with a growing wait: called every pass of the retries, so it must be
    /// cheap when there are none.
    /// </summary>
    void RemoveLeftovers()
    {
    }
}
