using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>One agent of a <see cref="ControlledProvider"/>.</summary>
public sealed class ControlledRun(AgentLaunch launch) : IAgentRun
{
    private readonly TaskCompletionSource<AgentOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentLaunch Launch { get; } = launch;

    public bool WasKilled { get; private set; }

    public string ProviderSessionId { get; } = $"controlled-{launch.SessionId:N}";

    public Task<AgentOutcome> Completion => _outcome.Task;

    public void End(AgentOutcome outcome) => _outcome.TrySetResult(outcome);

    public void Kill()
    {
        WasKilled = true;
        _outcome.TrySetResult(new AgentOutcome.Killed());
    }
}
