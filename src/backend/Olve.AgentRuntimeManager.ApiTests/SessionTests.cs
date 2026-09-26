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

    [Test]
    public async Task Create_WithAFreeSlot_Is201_AndResolvesDefaults()
    {
        var client = target.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/sessions", Wire.JsonContent(new
        {
            prompt = "Wait. fake:hang",
            tags = new Dictionary<string, string> { ["team"] = "arm" },
            env = new Dictionary<string, string> { ["A"] = "1" },
            secretEnv = new Dictionary<string, string> { ["TOKEN"] = "hunter2" },
        }));
        var session = (await response.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;
        await client.KillSessionAsync(session.Id);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(session.Status).IsEqualTo("working");
        await Assert.That(session.QueuePosition).IsNull();
        await Assert.That(session.Provider).IsEqualTo("fake");
        await Assert.That(session.Messaging).IsTrue();
        await Assert.That(session.Headless).IsFalse();
        await Assert.That(session.TimeoutSeconds).IsGreaterThan(0);
        await Assert.That(session.Tags).IsEquivalentTo(new Dictionary<string, string> { ["team"] = "arm" });
        await Assert.That(session.Env).IsEquivalentTo(new Dictionary<string, string> { ["A"] = "1" });
        await Assert.That(session.StartedAt).IsNotNull();
        await Assert.That(session.ProviderSessionId).IsNotNull();
    }

    [Test]
    public async Task SecretEnv_IsNeverReturned()
    {
        var client = target.CreateAuthenticatedClient();
        using var created = await client.PostAsync("/api/sessions", Wire.JsonContent(new { prompt = "fake:hang", secretEnv = new Dictionary<string, string> { ["TOKEN"] = "hunter2" } }));
        var session = (await created.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;

        var bodies = new[]
        {
            await created.Content.ReadAsStringAsync(),
            await client.GetStringAsync($"/api/sessions/{session.Id}"),
            await (await client.KillSessionAsync(session.Id)).Content.ReadAsStringAsync(),
        };

        foreach (var body in bodies)
        {
            await Assert.That(body).DoesNotContain("hunter2");
            await Assert.That(body).DoesNotContain("secretEnv");
        }
    }

    [Test]
    public async Task Session_RunsToCompletion()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync(new { prompt = "fake:sleep=0ms fake:exit=3 fake:summary=all_done" });

        var completed = await client.WaitForSessionAsync(session.Id, s => s.Status != "working");

        await Assert.That(completed.Status).IsEqualTo("completed");
        await Assert.That(completed.ExitCode).IsEqualTo(3);
        await Assert.That(completed.Summary).IsEqualTo("all done");
        await Assert.That(completed.EndedAt).IsNotNull();
    }

    [Test]
    public async Task Session_WhoseAgentFails_Fails()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync(new { prompt = "fake:sleep=0ms fake:fail=out_of_tokens" });

        var failed = await client.WaitForSessionAsync(session.Id, s => s.Status != "working");

        await Assert.That(failed.Status).IsEqualTo("failed");
        await Assert.That(failed.Error).IsEqualTo("out of tokens");
    }

    [Test]
    public async Task Session_PastItsTimeout_IsKilled()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync(new { prompt = "fake:hang", timeoutSeconds = 1 });

        var killed = await client.WaitForSessionAsync(session.Id, s => s.Status != "working");

        await Assert.That(killed.Status).IsEqualTo("killed");
        await Assert.That(killed.KillSource).IsEqualTo("timeout");
    }

    [Test]
    [Arguments("""{"prompt":""}""", "'prompt' must be at least 1 character.")]
    [Arguments("""{"prompt":"   "}""", "'prompt' cannot be blank.")]
    [Arguments("""{"prompt":"x","timeoutSeconds":0}""", "'timeoutSeconds' must be at least 1.")]
    [Arguments("""{"text":"x"}""", null)]
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
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions", Wire.JsonContent(new { prompt = "x", provider = "nope" }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("UNKNOWN_PROVIDER");
    }

    [Test]
    public async Task Create_WithAnAgentThatCantStart_IsCreatedAsFailed()
    {
        var client = target.CreateAuthenticatedClient();

        var session = await client.CreateSessionAsync(new { prompt = "fake:explode" });

        await Assert.That(session.Status).IsEqualTo("failed");
        await Assert.That(session.Error).Contains("fake:explode");
    }

    [Test]
    public async Task Create_WithTheSameIdempotencyKey_ReturnsTheOriginalSession()
    {
        var client = target.CreateAuthenticatedClient();
        var caller = UniqueCaller();
        var key = Guid.NewGuid().ToString();

        async Task<(HttpStatusCode, SessionBody)> Create()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = Wire.JsonContent(new { prompt = "fake:sleep=0ms", caller }) };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!);
        }

        var (firstStatus, first) = await Create();
        var (secondStatus, second) = await Create();

        await Assert.That(secondStatus).IsEqualTo(firstStatus);
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.CreatedAt).IsEqualTo(first.CreatedAt);
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

        using var first = await client.KillSessionAsync(session.Id, "enough");
        using var second = await client.KillSessionAsync(session.Id, "again");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var killed = (await first.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;
        await Assert.That(killed.Status).IsEqualTo("killed");
        await Assert.That(killed.KillReason).IsEqualTo("enough");
        await Assert.That(killed.KillSource).IsEqualTo("user");
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
    public async Task Search_FiltersAndPages_NewestFirst()
    {
        var client = target.CreateAuthenticatedClient();
        var caller = UniqueCaller();
        var first = await client.CreateSessionAsync(new { prompt = "Deploy it fake:sleep=0ms", caller, tags = new { team = "a" } });
        var second = await client.CreateSessionAsync(new { prompt = "deploy again fake:sleep=0ms", caller, tags = new { team = "a" } });
        await client.CreateSessionAsync(new { prompt = "unrelated fake:sleep=0ms", caller, tags = new { team = "b" } });

        var page = await Search(client, new { caller, tags = new { team = "a" }, text = "DEPLOY", limit = 1 });
        var next = await Search(client, new { caller, tags = new { team = "a" }, text = "DEPLOY", limit = 1, offset = 1 });

        await Assert.That(page.Total).IsEqualTo(2);
        await Assert.That(page.Limit).IsEqualTo(1);
        await Assert.That(page.Items.Single().Id).IsEqualTo(second.Id);
        await Assert.That(next.Items.Single().Id).IsEqualTo(first.Id);
        await Assert.That((await Search(client, new { caller })).Total).IsEqualTo(3);
    }

    [Test]
    public async Task Search_ByStatus()
    {
        var client = target.CreateAuthenticatedClient();
        var caller = UniqueCaller();
        var hanging = await client.CreateHangingSessionAsync(caller);
        var done = await client.CreateSessionAsync(new { prompt = "fake:sleep=0ms", caller });
        await client.WaitForSessionAsync(done.Id, s => s.Status == "completed");

        var working = await Search(client, new { caller, status = "working" });
        await client.KillSessionAsync(hanging.Id);

        await Assert.That(working.Items.Select(s => s.Id).ToList()).IsEquivalentTo([hanging.Id]);
    }

    [Test]
    [Arguments("""{"limit":0}""")]
    [Arguments("""{"limit":101}""")]
    [Arguments("""{"offset":-1}""")]
    [Arguments("""{"status":"sleeping"}""")]
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
