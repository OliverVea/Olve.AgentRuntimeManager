using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// Runs agents for one provider (<c>fake</c>, later <c>claude</c>, <c>codex</c>): the seam between
/// the session runtime and whatever actually executes an agent (OPEN-QUESTIONS A6).
/// </summary>
public interface IAgentProvider
{
    /// <summary>The name sessions select it by (<c>provider</c>).</summary>
    string Name { get; }

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
}
