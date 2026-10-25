using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    [Test]
    public async Task ByDefault_ClaudeOffersOpusSonnetAndHaiku()
    {
        using var services = Services([]);
        var claude = Provider(services, ClaudeProvider.ProviderName);

        await Assert.That(claude.DisplayName).IsEqualTo("Claude Code");
        await Assert.That(claude.Models.Select(m => (m.Id, m.DisplayName))).IsEquivalentTo([("opus", (string?)"Opus"), ("sonnet", "Sonnet"), ("haiku", "Haiku")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ByDefault_FakeOffersFake_AndClaudesModels()
    {
        using var services = Services([]);
        var fake = Provider(services, FakeProvider.ProviderName);

        await Assert.That(fake.Models.Select(m => m.Id)).IsEquivalentTo(["fake", "opus", "sonnet", "haiku"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>Arrays bind by index, onto what's there: a configured list must not merge into the defaults.</summary>
    [Test]
    public async Task ConfiguredModels_ReplaceTheDefaults()
    {
        using var services = Services(new()
        {
            ["Providers:Claude:DisplayName"] = "Claude (beta)",
            ["Providers:Claude:Models:0:Id"] = "claude-haiku-4-5",
            ["Providers:Claude:Models:0:DisplayName"] = "Haiku 4.5",
        });
        var claude = Provider(services, ClaudeProvider.ProviderName);

        await Assert.That(claude.DisplayName).IsEqualTo("Claude (beta)");
        await Assert.That(claude.Models.Select(m => (m.Id, m.DisplayName))).IsEquivalentTo([("claude-haiku-4-5", (string?)"Haiku 4.5")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConfiguredModels_KeepTheirOrder_AndMayLeaveOutTheDisplayName()
    {
        using var services = Services(new()
        {
            ["Providers:Fake:Models:0:Id"] = "b",
            ["Providers:Fake:Models:1:Id"] = "a",
        });
        var fake = Provider(services, FakeProvider.ProviderName);

        await Assert.That(fake.Models.Select(m => (m.Id, m.DisplayName))).IsEquivalentTo([("b", (string?)null), ("a", null)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Providers:Claude:Models:0:DisplayName", "No id")]
    [Arguments("Providers:Claude:Models:0:Id", " ")]
    public async Task AModelWithoutAnId_FailsValidation(string key, string value)
    {
        using var services = Services(new() { [key] = value });

        await Assert.That(() => services.GetRequiredService<IOptions<ClaudeProviderOptions>>().Value).Throws<OptionsValidationException>();
    }

    [Test]
    public async Task AModelTwice_FailsValidation()
    {
        using var services = Services(new()
        {
            ["Providers:Fake:Models:0:Id"] = "fake",
            ["Providers:Fake:Models:1:Id"] = "fake",
        });

        await Assert.That(() => services.GetRequiredService<IOptions<FakeProviderOptions>>().Value).Throws<OptionsValidationException>();
    }

    private static Type[] Providers(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSessionServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return [.. services.Where(d => d.ServiceType == typeof(IAgentProvider)).Select(d => d.ImplementationType!)];
    }

    private static IAgentProvider Provider(ServiceProvider services, string name) =>
        services.GetServices<IAgentProvider>().Single(p => p.Name == name);

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSessionServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }
}
