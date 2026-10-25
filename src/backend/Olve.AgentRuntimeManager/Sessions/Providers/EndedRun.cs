namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>An agent that ended while no server was watching; its outcome is known.</summary>
public sealed class EndedRun(string providerSessionId, AgentOutcome outcome) : IAgentRun
{
    public string ProviderSessionId => providerSessionId;

    /// <summary>Never: its outcome is known, and says the rest.</summary>
    public Task Answered { get; } = new TaskCompletionSource().Task;

    public Task<AgentOutcome> Completion { get; } = Task.FromResult(outcome);

    public void Kill()
    {
    }

    public bool TrySend(string text) => false;
}
