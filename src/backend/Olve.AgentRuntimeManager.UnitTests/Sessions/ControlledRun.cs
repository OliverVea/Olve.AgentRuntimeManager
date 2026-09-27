using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>One agent of a <see cref="ControlledProvider"/>.</summary>
public sealed class ControlledRun(AgentLaunch launch) : IAgentRun
{
    private readonly TaskCompletionSource<AgentOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentLaunch Launch { get; } = launch;

    public bool WasKilled { get; private set; }

    public string ProviderSessionId { get; } = launch.Resume ?? $"controlled-{launch.SessionId:N}";

    public Task<AgentOutcome> Completion => _outcome.Task;

    /// <summary>Whether it takes messages (<see cref="TrySend"/>); a test turns it off to act out a turn that just ended.</summary>
    public bool TakesMessages { get; set; } = true;

    /// <summary>The messages it took while running.</summary>
    public List<string> Sent { get; } = [];

    public void End(AgentOutcome outcome) => _outcome.TrySetResult(outcome);

    public void Kill()
    {
        WasKilled = true;
        _outcome.TrySetResult(new AgentOutcome.Killed());
    }

    public bool TrySend(string text)
    {
        if (!TakesMessages || _outcome.Task.IsCompleted)
        {
            return false;
        }

        Sent.Add(text);
        return true;
    }
}
