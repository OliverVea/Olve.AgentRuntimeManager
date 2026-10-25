using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Models as a server's configuration gives them: they replace the defaults, display names fall
/// back to the id (or name), and only those models are accepted. In process only (it needs its own
/// configuration).
/// </summary>
[ClassDataSource<ConfiguredModelsFactory>(Shared = SharedType.PerClass)]
public class ConfiguredModelTests(ConfiguredModelsFactory factory)
{
    private HttpClient Client()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only.");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Tokens.Mint());
        return client;
    }

    [Test]
    public async Task List_HasTheConfiguredModels_InOrder_WithDisplayNameFallbacks()
    {
        var providers = (await Client().GetFromJsonAsync<ProviderBody[]>("/api/providers", Wire.JsonOptions))!;

        await Assert.That(providers.Select(p => p.Name)).IsEquivalentTo(["claude", "fake"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var (claude, fake) = (providers[0], providers[1]);
        await Assert.That(claude.DisplayName).IsEqualTo("claude");
        await Assert.That(claude.Models).IsEquivalentTo([new ProviderModelBody("claude-opus-4-5", "claude-opus-4-5")]);
        await Assert.That(fake.DisplayName).IsEqualTo("Fake");
        await Assert.That(fake.Models).IsEquivalentTo(
            [new ProviderModelBody("small", "Small"), new ProviderModelBody("large", "large")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_WithAConfiguredModel_Works()
    {
        var session = await Client().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Model = "large" });

        await Assert.That(session.Model).IsEqualTo("large");
    }

    [Test]
    [Arguments("fake")]
    [Arguments("sonnet")]
    public async Task Create_WithADefaultModelTheConfigurationReplaced_Is400(string model)
    {
        using var response = await Client().PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("x") { Model = model }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("UNKNOWN_MODEL");
        await Assert.That(error.Message).IsEqualTo($"Provider 'fake' has no model '{model}'. Its models: small, large.");
    }
}
