using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

public class SessionServicesTests
{
    [Test]
    public async Task ByDefault_BothProvidersAreRegistered() =>
        await Assert.That(Providers([])).IsEquivalentTo([typeof(FakeProvider), typeof(ClaudeProvider)]);

    [Test]
    public async Task FakeDisabled_LeavesOnlyClaude() =>
        await Assert.That(Providers(new() { ["Providers:Fake:Enabled"] = "false" })).IsEquivalentTo([typeof(ClaudeProvider)]);

    private static Type[] Providers(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSessionServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return [.. services.Where(d => d.ServiceType == typeof(IAgentProvider)).Select(d => d.ImplementationType!)];
    }
}
