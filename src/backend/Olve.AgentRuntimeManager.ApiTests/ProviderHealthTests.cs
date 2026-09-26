using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary><c>GET /api/providers/health</c>: read-only, so it runs against any target.</summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class ProviderHealthTests(ApiTarget target)
{
    [Test]
    public async Task Health_ListsEveryProvider_ByName()
    {
        var health = (await target.CreateAuthenticatedClient().GetFromJsonAsync<ProviderHealthBody[]>("/api/providers/health", Wire.JsonOptions))!;

        await Assert.That(health.Select(h => h.Provider)).Contains("claude");
        await Assert.That(health.Select(h => h.Provider)).IsOrderedBy(p => p, StringComparer.Ordinal);
        await Assert.That(health.All(h => h.Status is "available" or "limited" or "unreachable" or "unauthorized")).IsTrue();
    }

    [Test]
    public async Task Health_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync("/api/providers/health");

        await Assert.That((int)response.StatusCode).IsEqualTo(401);
    }
}
