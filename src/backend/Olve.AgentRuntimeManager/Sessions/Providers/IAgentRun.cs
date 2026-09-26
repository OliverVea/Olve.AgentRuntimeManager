namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>One running agent.</summary>
public interface IAgentRun
{
    /// <summary>The provider's own id for the agent.</summary>
    string ProviderSessionId { get; }

    /// <summary>Completes when the agent ends, however it ends (never faults).</summary>
    Task<AgentOutcome> Completion { get; }

    /// <summary>Stops the agent; <see cref="Completion"/> then completes with <see cref="AgentOutcome.Killed"/>.</summary>
    void Kill();
}
