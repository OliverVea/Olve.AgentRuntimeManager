using Microsoft.Extensions.Options;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// A provider that runs no LLM: each agent follows the <see cref="FakeScript"/> in its prompt. The
/// default provider until real ones land (M9), and what every test runs against.
/// </summary>
public sealed class FakeProvider(TimeProvider time, IOptions<FakeProviderOptions> options) : IAgentProvider
{
    public const string ProviderName = "fake";

    public string Name => ProviderName;

    public IAgentRun Start(AgentLaunch launch) =>
        new FakeRun(FakeScript.Parse(launch.Prompt, options.Value.Delay), time);

    private sealed class FakeRun : IAgentRun
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
                : new AgentOutcome.Completed(script.ExitCode, script.Summary ?? "Fake agent finished.");
        }
    }
}
