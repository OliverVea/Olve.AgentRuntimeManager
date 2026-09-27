namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>An agent that ended while no server was watching; its outcome is known.</summary>
public sealed class EndedRun(string providerSessionId, AgentOutcome outcome) : IAgentRun
{
    public string ProviderSessionId => providerSessionId;

    public Task<AgentOutcome> Completion { get; } = Task.FromResult(outcome);

    public void Kill()
    {
    }

    public bool TrySend(string text) => false;
}
