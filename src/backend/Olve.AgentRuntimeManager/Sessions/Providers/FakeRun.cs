namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>One <see cref="FakeProvider"/> agent: waits out its <see cref="FakeScript"/>, or until killed.</summary>
internal sealed class FakeRun : IAgentRun
{
    // Never disposed: Kill may come after the run ended, and a CTS without a timer holds nothing.
    private readonly CancellationTokenSource _kill = new();

    public FakeRun(FakeScript script, TimeProvider time)
    {
        Completion = RunAsync(script, time);
    }

    public string ProviderSessionId { get; } = $"fake-{Guid.NewGuid():N}";

    public Task<AgentOutcome> Completion { get; }

    public void Kill() => _kill.Cancel();

    private async Task<AgentOutcome> RunAsync(FakeScript script, TimeProvider time)
    {
        try
        {
            await Task.Delay(script.Hang ? Timeout.InfiniteTimeSpan : script.Delay, time, _kill.Token);
        }
        catch (OperationCanceledException)
        {
            return new AgentOutcome.Killed();
        }

        return script.Failure is { } failure
            ? new AgentOutcome.Failed(failure)
            : new AgentOutcome.Completed(script.ExitCode);
    }
}
