using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// The session API over HTTP. Sessions run on the fake provider, steered by <c>fake:</c> directives
/// in the prompt; tests that leave an agent hanging kill it. The target is shared (and other
/// tests run concurrently), so each test only looks at its own sessions (a unique <c>caller</c>).
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class SessionTests(ApiTarget target)
{
    private static string UniqueCaller() => $"api-test-{Guid.NewGuid():N}";

    /// <summary>Terminal: on a busy target a session may be queued before it works, so "not working" isn't enough.</summary>
    private static bool Ended(SessionBody session) => session.Status is "completed" or "failed" or "killed";

    [Test]
    public async Task Create_WithAFreeSlot_Is201()
    {
        var client = target.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("Wait. fake:hang", "creator") { Model = "fake-large" }));
        var body = await response.Content.ReadAsStringAsync();
        var created = System.Text.Json.JsonSerializer.Deserialize<CreatedSessionBody>(body, Wire.JsonOptions)!;
        var session = await client.GetSessionAsync(created.Id);
        await client.KillSessionAsync(session.Id);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        // Only the id: the session itself is read with GET.
        await Assert.That(System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject().Select(p => p.Key)).IsEquivalentTo(["id"]);
        await Assert.That(session.Status).IsEqualTo("working");
        await Assert.That(session.QueuePosition).IsNull();
        await Assert.That(session.Provider).IsEqualTo("fake");
        await Assert.That(session.Model).IsEqualTo("fake-large");
        await Assert.That(session.Caller).IsEqualTo("creator");
        await Assert.That(session.TimeoutSeconds).IsNull();
        await Assert.That(session.StartedAt).IsNotNull();
        await Assert.That(session.ProviderSessionId).IsNotNull();
    }

    [Test]
    public async Task Session_RunsToCompletion()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms fake:exit=3");

        var completed = await client.WaitForSessionAsync(session.Id, Ended);

        await Assert.That(completed.Status).IsEqualTo("completed");
        await Assert.That(completed.ExitCode).IsEqualTo(3);
        await Assert.That(completed.EndedAt).IsNotNull();
    }

    [Test]
    public async Task Session_WhoseAgentFails_Fails()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms fake:fail=out_of_tokens");

        var failed = await client.WaitForSessionAsync(session.Id, Ended);

        await Assert.That(failed.Status).IsEqualTo("failed");
        await Assert.That(failed.Error).IsEqualTo("out of tokens");
    }

    [Test]
    public async Task Session_PastItsTimeout_IsKilled()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:hang", timeoutSeconds: 1);

        var killed = await client.WaitForSessionAsync(session.Id, Ended);

        await Assert.That(killed.Status).IsEqualTo("killed");
        await Assert.That(killed.KillSource).IsEqualTo("timeout");
    }

    [Test]
    [Arguments("""{"prompt":"","provider":"fake","model":"fake","caller":"c"}""", "'prompt' must be at least 1 character.")]
    [Arguments("""{"prompt":"   ","provider":"fake","model":"fake","caller":"c"}""", "'prompt' cannot be blank.")]
    [Arguments("""{"prompt":"x","provider":"fake","model":"fake","caller":"c","timeoutSeconds":0}""", "'timeoutSeconds' must be at least 1.")]
    [Arguments("""{"prompt":"x","provider":"fake","model":"fake","caller":""}""", "'caller' must be at least 1 character.")]
    [Arguments("""{"prompt":"x","provider":"fake","caller":"c"}""", null)]
    [Arguments("""{"prompt":"x","model":"fake","caller":"c"}""", null)]
    [Arguments("""{""", null)]
    public async Task Create_WithAnInvalidBody_Is400(string body, string? message)
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions", Wire.Json(body));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("INVALID_REQUEST");
        if (message is not null)
        {
            await Assert.That(error.Message).IsEqualTo(message);
        }
    }

    [Test]
    public async Task Create_WithAnUnknownProvider_Is400()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("x") { Provider = "nope" }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("UNKNOWN_PROVIDER");
    }

    [Test]
    public async Task Create_WithAnAgentThatCantStart_IsCreatedAsFailed()
    {
        var client = target.CreateAuthenticatedClient();

        var session = await client.CreateSessionAsync("fake:explode");

        await Assert.That(session.Status).IsEqualTo("failed");
        await Assert.That(session.Error).Contains("fake:explode");
    }

    [Test]
    public async Task Create_WithTheSameIdempotencyKey_ReturnsTheOriginalSession()
    {
        var client = target.CreateAuthenticatedClient();
        var caller = UniqueCaller();
        var key = Guid.NewGuid().ToString();

        async Task<(HttpStatusCode, CreatedSessionBody)> Create()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms", caller)) };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<CreatedSessionBody>(Wire.JsonOptions))!);
        }

        var (firstStatus, first) = await Create();
        var (secondStatus, second) = await Create();

        await Assert.That(secondStatus).IsEqualTo(firstStatus);
        await Assert.That(second.Id).IsEqualTo(first.Id);
        var page = await Search(client, new { caller });
        await Assert.That(page.Total).IsEqualTo(1);
    }

    [Test]
    public async Task Get_AnUnknownSession_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().GetAsync($"/api/sessions/{Guid.NewGuid()}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SESSION_NOT_FOUND");
    }

    [Test]
    [Arguments("GET", "/api/sessions/not-a-uuid")]
    [Arguments("POST", "/api/sessions/not-a-uuid/kill")]
    [Arguments("DELETE", "/api/sessions/not-a-uuid")]
    public async Task AMalformedId_Is400(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? Wire.Json("{}") : null };

        using var response = await target.CreateAuthenticatedClient().SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    [Test]
    public async Task Kill_KillsOnce_ThenIs409()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateHangingSessionAsync();

        using var first = await client.KillSessionAsync(session.Id, "enough", caller: "killer");
        using var second = await client.KillSessionAsync(session.Id, "again");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var killed = (await first.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;
        await Assert.That(killed.Status).IsEqualTo("killed");
        await Assert.That(killed.KillReason).IsEqualTo("enough");
        await Assert.That(killed.KillSource).IsEqualTo("user");
        await Assert.That(killed.KillCaller).IsEqualTo("killer");
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await second.ErrorAsync()).Code).IsEqualTo("SESSION_ALREADY_ENDED");
        await Assert.That((await client.GetSessionAsync(session.Id)).KillReason).IsEqualTo("enough");
    }

    [Test]
    public async Task Kill_AnUnknownSession_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().KillSessionAsync(Guid.NewGuid());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Delete_OnlyEndedSessions()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateHangingSessionAsync();

        using var whileWorking = await client.DeleteAsync($"/api/sessions/{session.Id}");
        await client.KillSessionAsync(session.Id);
        using var afterKill = await client.DeleteAsync($"/api/sessions/{session.Id}");
        using var afterDelete = await client.GetAsync($"/api/sessions/{session.Id}");
        using var again = await client.DeleteAsync($"/api/sessions/{session.Id}");

        await Assert.That(whileWorking.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await whileWorking.ErrorAsync()).Code).IsEqualTo("SESSION_NOT_ENDED");
        await Assert.That(afterKill.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterDelete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Search_FiltersByCallerAndPages_NewestFirst()
    {
        var client = target.CreateAuthenticatedClient();
        var caller = UniqueCaller();
        var first = await client.CreateSessionAsync("fake:sleep=0ms", caller);
        var second = await client.CreateSessionAsync("fake:sleep=0ms", caller);
        await client.CreateSessionAsync("fake:sleep=0ms", UniqueCaller());

        var page = await Search(client, new { caller, limit = 1 });
        var next = await Search(client, new { caller, limit = 1, offset = 1 });

        await Assert.That(page.Total).IsEqualTo(2);
        await Assert.That(page.Limit).IsEqualTo(1);
        await Assert.That(page.Items.Single().Id).IsEqualTo(second.Id);
        await Assert.That(next.Items.Single().Id).IsEqualTo(first.Id);
    }

    [Test]
    public async Task Search_ByStatus()
    {
        var client = target.CreateAuthenticatedClient();
        var caller = UniqueCaller();
        var hanging = await client.CreateHangingSessionAsync(caller);
        var done = await client.CreateSessionAsync("fake:sleep=0ms", caller);
        await client.WaitForSessionAsync(done.Id, s => s.Status == "completed");

        var working = await Search(client, new { caller, status = new[] { "working" } });
        var either = await Search(client, new { caller, status = new[] { "working", "completed" } });
        await client.KillSessionAsync(hanging.Id);

        await Assert.That(working.Items.Select(s => s.Id).ToList()).IsEquivalentTo([hanging.Id]);
        await Assert.That(either.Items.Select(s => s.Id).ToList()).IsEquivalentTo([hanging.Id, done.Id]);
    }

    [Test]
    [Arguments("""{"limit":0}""")]
    [Arguments("""{"limit":101}""")]
    [Arguments("""{"offset":-1}""")]
    [Arguments("""{"status":["sleeping"]}""")]
    [Arguments("""{"status":[]}""")]
    [Arguments("""{"status":"working"}""")]
    public async Task Search_WithInvalidFilters_Is400(string body)
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions/search", Wire.Json(body));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    private static async Task<SessionPageBody> Search(HttpClient client, object body)
    {
        using var response = await client.PostAsync("/api/sessions/search", Wire.JsonContent(body));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionPageBody>(Wire.JsonOptions))!;
    }
}
