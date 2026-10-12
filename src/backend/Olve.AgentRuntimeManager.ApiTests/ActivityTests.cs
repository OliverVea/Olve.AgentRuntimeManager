using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// <c>GET /api/sessions/{id}/activity</c> on the fake provider, whose directives script the
/// conversation it's picked from (<c>fake:say=</c>, <c>fake:tool=</c>, <c>fake:toolerror=</c>, …).
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class ActivityTests(ApiTarget target)
{
    private const int MaxLength = 200;

    /// <summary>A completed session whose conversation has one of everything.</summary>
    private const string Everything =
        "Go. fake:sleep=0ms fake:say=Looking fake:think=Hmm fake:notice=Ping fake:tool=Bash fake:toolerror=Read " +
        "fake:subagent=Sub_words fake:say=All_done";

    private static async Task<ActivityLineBody[]> ActivityAsync(HttpClient client, Guid id, string query = "")
    {
        using var response = await client.GetAsync($"/api/sessions/{id}/activity{query}");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Activity answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<ActivityBody>(Wire.JsonOptions))!.Lines;
    }

    private static async Task<SessionBody> CompletedAsync(HttpClient client, string prompt)
    {
        var session = await client.CreateSessionAsync(prompt);
        return await client.WaitForSessionAsync(session.Id, s => s.Status == "completed");
    }

    [Test]
    public async Task Lines_AreTheAgentsWords_ItsToolCalls_AndWhatFailed_OldestFirst()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await CompletedAsync(client, Everything);

        var lines = await ActivityAsync(client, session.Id, "?limit=50");

        // Left out: the prompt, thinking, the notice, results that didn't fail, the turn's end, and
        // everything the subagent did (its words, its tool call and its failed result).
        await Assert.That(lines).IsEquivalentTo(
        [
            new ActivityLineBody("text", "Looking"),
            new ActivityLineBody("tool", """Bash → {"fake":true}"""),
            new ActivityLineBody("tool", """Read → {"fake":true}"""),
            new ActivityLineBody("error", "Read failed."),
            new ActivityLineBody("tool", """Task → {"fake":true}"""),
            new ActivityLineBody("text", "All done"),
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Limit_DefaultsTo3_AndIsTheLatestLines()
    {
        var client = target.CreateAuthenticatedClient();
        var says = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"fake:say=Line_{i}"));
        var session = await CompletedAsync(client, $"fake:sleep=0ms {says}");

        var byDefault = await ActivityAsync(client, session.Id);
        var one = await ActivityAsync(client, session.Id, "?limit=1");
        var most = await ActivityAsync(client, session.Id, "?limit=50");

        await Assert.That(byDefault.Select(l => l.Text)).IsEquivalentTo(["Line 58", "Line 59", "Line 60"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(one.Select(l => l.Text)).IsEquivalentTo(["Line 60"]);
        await Assert.That(most.Length).IsEqualTo(50);
        await Assert.That((most[0].Text, most[^1].Text)).IsEqualTo(("Line 11", "Line 60"));
    }

    [Test]
    [Arguments("?limit=0", "'limit' must be at least 1.")]
    [Arguments("?limit=51", "'limit' cannot exceed 50.")]
    [Arguments("?limit=-3", "'limit' must be at least 1.")]
    [Arguments("?limit=three", null)]
    public async Task LimitOutOfRange_Is400(string query, string? message)
    {
        var client = target.CreateAuthenticatedClient();
        var session = await CompletedAsync(client, "fake:sleep=0ms fake:say=Hi");

        using var response = await client.GetAsync($"/api/sessions/{session.Id}/activity{query}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("INVALID_REQUEST");
        if (message is not null)
        {
            await Assert.That(error.Message).IsEqualTo(message);
        }
    }

    [Test]
    public async Task LongAndMultilineText_IsOneBoundedLine()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await CompletedAsync(client, $"fake:sleep=0ms fake:say={new string('a', 300)} fake:say=\\n\\nFirst_line\\nSecond_line");

        var lines = await ActivityAsync(client, session.Id);

        await Assert.That(lines[0].Text).IsEqualTo(new string('a', MaxLength - 1) + "…");
        await Assert.That(lines[1].Text).IsEqualTo("First line");
    }

    [Test]
    public async Task AgentThatSaidNothingYet_HasNoLines()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateHangingSessionAsync();

        try
        {
            await Assert.That(await ActivityAsync(client, session.Id)).IsEmpty();
        }
        finally
        {
            (await client.KillSessionAsync(session.Id)).EnsureSuccessStatusCode();
        }
    }

    [Test]
    public async Task FailedSession_EndsWithItsError()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms fake:say=Reading fake:fail=out_of_tokens");
        await client.WaitForSessionAsync(session.Id, s => s.Status == "failed");

        // Its turn failed with the same error: once.
        await Assert.That(await ActivityAsync(client, session.Id)).IsEquivalentTo(
            [new ActivityLineBody("text", "Reading"), new ActivityLineBody("error", "out of tokens")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task SessionThatFailedToStart_HasOnlyItsError()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:bogus");
        var failed = await client.WaitForSessionAsync(session.Id, s => s.Status == "failed");

        await Assert.That(await ActivityAsync(client, session.Id)).IsEquivalentTo([new ActivityLineBody("error", failed.Error!)]);
    }

    [Test]
    public async Task FailedTurn_OfASessionThatContinued_IsAnError()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms fake:fail=boom");
        await client.WaitForSessionAsync(session.Id, s => s.Status == "failed");

        await Assert.That(await client.SentMessageAsync(session.Id, "fake:hang fake:say=Again")).IsEqualTo("continued");
        try
        {
            await client.WaitForSessionAsync(session.Id, s => s.Status == "working");
            await Assert.That(await ActivityAsync(client, session.Id)).IsEquivalentTo(
                [new ActivityLineBody("error", "boom"), new ActivityLineBody("text", "Again")],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            (await client.KillSessionAsync(session.Id)).EnsureSuccessStatusCode();
        }
    }

    [Test]
    public async Task UnknownSession_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().GetAsync($"/api/sessions/{Guid.NewGuid()}/activity");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SESSION_NOT_FOUND");
    }

    [Test]
    public async Task Activity_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync($"/api/sessions/{Guid.NewGuid()}/activity");

        await Assert.That((int)response.StatusCode).IsEqualTo(401);
    }
}
