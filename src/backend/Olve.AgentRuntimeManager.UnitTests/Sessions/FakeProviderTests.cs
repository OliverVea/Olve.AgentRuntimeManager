using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>Whether the fake provider answers its agents (<see cref="IAgentRun.Answered"/>), LLM-free.</summary>
public class FakeProviderTests
{
    private readonly FakeProvider _provider = new(TimeProvider.System, Options.Create(new FakeProviderOptions()));

    private static AgentLaunch Launch(string prompt) => new(Guid.NewGuid(), prompt, "m", 1, Guid.NewGuid(), Guid.NewGuid());

    [Test]
    public async Task AnAgentItRuns_IsAnsweredAtOnce_WhileItRunsOn()
    {
        var run = _provider.Start(Launch("fake:hang"));

        await Assert.That(run.Answered.IsCompletedSuccessfully).IsTrue();
        await Assert.That(run.Completion.IsCompleted).IsFalse();
        run.Kill();
        await Assert.That(await run.Completion).IsTypeOf<AgentOutcome.Killed>();
    }

    [Test]
    public async Task AnAgentItRefuses_IsNeverAnswered()
    {
        var run = _provider.Start(Launch("fake:down=unreachable"));

        await Assert.That(await run.Completion).IsTypeOf<AgentOutcome.Unavailable>();
        await Assert.That(run.Answered.IsCompleted).IsFalse();
    }
}
