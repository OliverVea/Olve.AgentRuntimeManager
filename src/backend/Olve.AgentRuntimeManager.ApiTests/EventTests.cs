using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// <c>GET /api/events</c>: the SSE stream of session lifecycle events, its heartbeats, filters and
/// <c>Last-Event-ID</c> replay. Needs a token (the stream is authenticated), so on a base-URL
/// target without one these skip; the 401 is covered by <see cref="AuthTests"/>.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class EventTests(ApiTarget target)
{
    private static bool IsHeartbeat(ReceivedEvent e) => e.Event == "heartbeat";

    private static Func<ReceivedEvent, bool> About(SessionBody session, string type) =>
        e => e.Event == type && e.SessionId == session.Id;

    [Test]
    public async Task Connect_SendsAHeartbeatWithoutAnId()
    {
        await using var events = await EventStream.OpenAsync(target.CreateAuthenticatedClient());

        var first = await events.NextAsync();

        await Assert.That(first.Event).IsEqualTo("heartbeat");
        await Assert.That(first.Id).IsNull();
        await Assert.That(first.Type).IsEqualTo("heartbeat");
        await Assert.That(first.Data.GetProperty("at").GetDateTimeOffset().Offset).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task SessionLifecycle_IsStreamedWithIdsTypesAndSubjects()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client);
        await events.NextAsync(IsHeartbeat); // connected: later events are live

        var session = await client.CreateHangingSessionAsync();
        session = await client.WaitForSessionAsync(session.Id, s => s.Status == "working"); // a shared target may queue it first
        (await client.KillSessionAsync(session.Id, "enough")).EnsureSuccessStatusCode();

        var created = await events.NextAsync(About(session, "session.created"));
        var started = await events.NextAsync(About(session, "session.started"));
        var killed = await events.NextAsync(About(session, "session.killed"));

        foreach (var e in new[] { created, started, killed })
        {
            await Assert.That(e.Type).IsEqualTo(e.Event);
            await Assert.That(e.Id).IsNotNull();
            await Assert.That(e.Data.TryGetProperty("at", out _)).IsTrue();
        }

        await Assert.That(long.Parse(started.Id!)).IsGreaterThan(long.Parse(created.Id!));
        await Assert.That(long.Parse(killed.Id!)).IsGreaterThan(long.Parse(started.Id!));
        var createdSession = created.Data.GetProperty("session").Deserialize<SessionBody>(Wire.JsonOptions)!;
        await Assert.That(createdSession.Id).IsEqualTo(session.Id);
        await Assert.That(createdSession.Status).IsEqualTo("queued");
        await Assert.That(started.Data.GetProperty("previous").GetString()).IsEqualTo("queued");
        await Assert.That(started.Data.GetProperty("providerSessionId").GetString()).IsEqualTo(session.ProviderSessionId);
        await Assert.That(killed.Data.GetProperty("previous").GetString()).IsEqualTo("working");
        await Assert.That(killed.Data.GetProperty("reason").GetString()).IsEqualTo("enough");
        await Assert.That(killed.Data.GetProperty("source").GetString()).IsEqualTo("user");
    }

    [Test]
    public async Task CompletedSession_IsStreamed()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client, "?event=session.completed");
        await events.NextAsync(IsHeartbeat);

        var session = await client.CreateSessionAsync(new { prompt = "fake:sleep=0ms fake:exit=2 fake:summary=all_done" });

        var completed = await events.NextAsync(About(session, "session.completed"));
        await Assert.That(completed.Data.GetProperty("exitCode").GetInt32()).IsEqualTo(2);
        await Assert.That(completed.Data.GetProperty("summary").GetString()).IsEqualTo("all done");
    }

    [Test]
    public async Task EventFilter_StreamsOnlyTheNamedEvents()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client, "?event=session.killed,session.started");
        await events.NextAsync(IsHeartbeat);

        var session = await client.CreateHangingSessionAsync();
        (await client.KillSessionAsync(session.Id)).EnsureSuccessStatusCode();

        var received = await events.UntilAsync(About(session, "session.killed"));

        await Assert.That(received.Where(e => !IsHeartbeat(e)).Select(e => e.Event).Distinct().Except(["session.killed", "session.started"]))
            .IsEmpty();
    }

    [Test]
    public async Task ExcludeFilter_DropsNamedEvents_AfterANamespaceInclude()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client, "?event=session.*&exclude_event=session.created,session.started");
        await events.NextAsync(IsHeartbeat);

        var session = await client.CreateHangingSessionAsync();
        (await client.KillSessionAsync(session.Id)).EnsureSuccessStatusCode();

        var received = await events.UntilAsync(About(session, "session.killed"));

        // session.queued may pass too, on a target whose slots are busy.
        await Assert.That(received.Where(e => !IsHeartbeat(e)).Select(e => e.Event).Intersect(["session.created", "session.started"])).IsEmpty();
        await Assert.That(received[^1].Event).IsEqualTo("session.killed");
    }

    [Test]
    [Arguments("?event=session.exploded", "'event' has unknown event 'session.exploded'")]
    [Arguments("?exclude_event=heartbeat", "'exclude_event' has unknown event 'heartbeat'")]
    public async Task UnknownEventName_Is400(string query, string expected)
    {
        using var response = await EventStream.SendAsync(target.CreateAuthenticatedClient(), query);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("INVALID_REQUEST");
        await Assert.That(error.Message).StartsWith(expected);
    }

    [Test]
    public async Task MalformedLastEventId_Is400()
    {
        using var response = await EventStream.SendAsync(target.CreateAuthenticatedClient(), lastEventId: "not-an-id");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Message)
            .IsEqualTo("'Last-Event-ID' is not an event id of this stream: 'not-an-id'.");
    }

    [Test]
    public async Task LastEventId_ReplaysWhatWasMissed()
    {
        var client = target.CreateAuthenticatedClient();
        SessionBody seen;
        string seenId;
        await using (var events = await EventStream.OpenAsync(client))
        {
            await events.NextAsync(IsHeartbeat);
            seen = await client.CreateSessionAsync(new { prompt = "seen fake:sleep=0ms" });
            seenId = (await events.NextAsync(About(seen, "session.created"))).Id!;
        }

        var missed = await client.CreateSessionAsync(new { prompt = "missed fake:sleep=0ms" });

        // Replayed events come before the connect heartbeat.
        await using var resumed = await EventStream.OpenAsync(client, lastEventId: seenId);
        var replayed = await resumed.UntilAsync(IsHeartbeat);

        await Assert.That(replayed.Any(About(missed, "session.created"))).IsTrue();
        await Assert.That(replayed.Any(About(seen, "session.created"))).IsFalse();
        await Assert.That(replayed.Where(e => !IsHeartbeat(e)).All(e => long.Parse(e.Id!) > long.Parse(seenId))).IsTrue();
    }

    [Test]
    public async Task WithoutLastEventId_NothingIsReplayed()
    {
        var client = target.CreateAuthenticatedClient();
        var earlier = await client.CreateSessionAsync(new { prompt = "before connecting fake:sleep=0ms" });

        await using var events = await EventStream.OpenAsync(client);
        var beforeHeartbeat = await events.UntilAsync(IsHeartbeat);

        await Assert.That(beforeHeartbeat).HasSingleItem();
        await Assert.That(beforeHeartbeat.Any(About(earlier, "session.created"))).IsFalse();
    }
}
