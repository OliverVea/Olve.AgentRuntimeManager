using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>Sessions outlive the server: a second host on the same database. In process only.</summary>
public class RestartTests
{
    [Test]
    public async Task AfterARestart_EndedSessionsAreThere_AndWorkingOnesWereKilled()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only.");
        var folder = Directory.CreateTempSubdirectory("arm-restart-").FullName;
        try
        {
            SessionBody done, hanging;
            await using (var before = new RestartedFactory(folder))
            {
                var client = Client(before);
                done = await client.CreateSessionAsync("fake:sleep=0ms", "restart-test");
                await client.WaitForSessionAsync(done.Id, s => s.Status == "completed");
                hanging = await client.CreateSessionAsync("fake:hang", "restart-test");
            }

            await using var after = new RestartedFactory(folder);
            var restarted = Client(after);
            var completed = await restarted.GetSessionAsync(done.Id);
            var killed = await restarted.GetSessionAsync(hanging.Id);
            using var search = await restarted.PostAsync("/api/sessions/search", Wire.JsonContent(new { caller = "restart-test" }));
            var page = (await search.Content.ReadFromJsonAsync<SessionPageBody>(Wire.JsonOptions))!;

            await Assert.That(completed.Status).IsEqualTo("completed");
            await Assert.That(killed.Status).IsEqualTo("killed");
            await Assert.That(killed.KillSource).IsEqualTo("system");
            await Assert.That(page.Total).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static HttpClient Client(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Tokens.Mint());
        return client;
    }
}
