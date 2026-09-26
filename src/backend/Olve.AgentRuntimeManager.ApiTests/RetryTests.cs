using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Sessions a provider refuses (<c>fake:down=…</c>) retry once it may be tried again. In process
/// only: pausing a provider pauses it for everyone on the server, so never against a shared one.
/// </summary>
[ClassDataSource<ShortBackoffFactory>(Shared = SharedType.PerClass)]
[NotInParallel]
public class RetryTests(ShortBackoffFactory factory)
{
    private HttpClient Client()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only.");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Tokens.Mint());
        return client;
    }

    [Test]
    public async Task RefusedOnce_RetriesAndCompletes()
    {
        var client = Client();

        var session = await client.CreateSessionAsync("fake:down=unreachable:1 fake:sleep=10ms");

        var completed = await client.WaitForSessionAsync(session.Id, s => s.Status == "completed");
        await Assert.That(completed.Attempts).IsEqualTo(2);
        await Assert.That(completed.Error).IsNull();
        var fake = await FakeHealth(client);
        await Assert.That(fake.Status).IsEqualTo("available");
    }

    [Test]
    public async Task RefusedEveryTime_FailsAfterItsRetries_WithTheProvidersError()
    {
        var client = Client();

        var session = await client.CreateSessionAsync("fake:down=unreachable");

        var failed = await client.WaitForSessionAsync(session.Id, s => s.Status == "failed");
        await Assert.That(failed.Attempts).IsEqualTo(3);
        await Assert.That(failed.Error).IsEqualTo("Fake API unreachable.");
        var fake = await FakeHealth(client);
        await Assert.That(fake.Status).IsEqualTo("unreachable");
        await Assert.That(fake.Reason).IsEqualTo("Fake API unreachable.");

        // Let the next test find it available: a probe that gets through.
        var probe = await client.CreateSessionAsync("fake:sleep=10ms");
        await client.WaitForSessionAsync(probe.Id, s => s.Status == "completed");
        await Assert.That((await FakeHealth(client)).Status).IsEqualTo("available");
    }

    private static async Task<ProviderHealthBody> FakeHealth(HttpClient client) =>
        (await client.GetFromJsonAsync<ProviderHealthBody[]>("/api/providers/health", Wire.JsonOptions))!.Single(h => h.Provider == "fake");
}
