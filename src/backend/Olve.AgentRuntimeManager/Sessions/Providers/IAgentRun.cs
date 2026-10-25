namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>One running agent.</summary>
public interface IAgentRun
{
    /// <summary>The provider's own id for the agent.</summary>
    string ProviderSessionId { get; }

    /// <summary>
    /// Whether the agent itself started (so its provider session exists): false for one that never
    /// got that far, e.g. its repositories' checkout failed or was killed (M5e). A session whose
    /// agent never started doesn't resume it later; it starts afresh.
    /// </summary>
    bool AgentStarted => true;

    /// <summary>
    /// Completes once the provider has answered the agent (for Claude: the model's first message),
    /// proof that the provider works while the agent may still run for long; never, for an agent
    /// refused or that never got that far. Never faults. Only an unreachable provider's probe
    /// reads it (<see cref="ProviderState"/>).
    /// </summary>
    Task Answered { get; }

    /// <summary>Completes when the agent ends, however it ends (never faults).</summary>
    Task<AgentOutcome> Completion { get; }

    /// <summary>Stops the agent; <see cref="Completion"/> then completes with <see cref="AgentOutcome.Killed"/>.</summary>
    void Kill();

    /// <summary>
    /// Gives the agent a message (M11): it sees it after its current step. False when it can't take
    /// one any more (its input is closed, it's ending or it has ended), and then it never sees it.
    /// Must not block: it's called while the runtime holds its lock.
    /// </summary>
    bool TrySend(string text);
}
