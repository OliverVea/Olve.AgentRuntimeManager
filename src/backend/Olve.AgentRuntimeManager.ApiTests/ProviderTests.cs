using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary><c>GET /api/providers</c>: read-only, so it runs against any target (whose models are its own).</summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class ProviderTests(ApiTarget target)
{
    [Test]
    public async Task List_HasEveryProvider_ByName_WithItsModels()
    {
        var providers = (await target.CreateAuthenticatedClient().GetFromJsonAsync<ProviderBody[]>("/api/providers", Wire.JsonOptions))!;

        await Assert.That(providers.Select(p => p.Name)).Contains("claude");
        await Assert.That(providers.Select(p => p.Name)).IsOrderedBy(p => p, StringComparer.Ordinal);
        await Assert.That(providers.All(p => p.DisplayName.Length > 0 && p.Models.Count > 0)).IsTrue();
        await Assert.That(providers.SelectMany(p => p.Models).All(m => m.Id.Length > 0 && m.DisplayName.Length > 0)).IsTrue();
    }

    [Test]
    public async Task List_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync("/api/providers");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task EveryListedModel_CanStartASession()
    {
        var client = target.CreateAuthenticatedClient();
        var fake = (await client.GetFromJsonAsync<ProviderBody[]>("/api/providers", Wire.JsonOptions))!.SingleOrDefault(p => p.Name == "fake");
        Skip.When(fake is null, "The target has no fake provider.");

        foreach (var model in fake!.Models)
        {
            var session = await client.CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Model = model.Id });

            await Assert.That(session.Model).IsEqualTo(model.Id);
        }
    }
}
