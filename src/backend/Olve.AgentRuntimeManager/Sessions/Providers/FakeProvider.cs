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
}
