using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// <c>GET /api/events</c>: the SSE stream of message changes, its heartbeats, filters and
/// <c>Last-Event-ID</c> replay. Needs a token (the stream is authenticated), so on a base-URL
/// target without one these skip; the 401 is covered by <see cref="AuthTests"/>.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class EventTests(ApiTarget target)
{
    private static bool IsHeartbeat(ReceivedEvent e) => e.Event == "heartbeat";

    private static Func<ReceivedEvent, bool> About(MessageBody message, string type) =>
        e => e.Event == type && e.MessageId == message.Id;

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
    public async Task CreateUpdateDelete_AreStreamedWithIdsTypesAndSubjects()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client);
        await events.NextAsync(IsHeartbeat); // connected: later events are live

        var created = await client.CreateMessageAsync("streamed");
        (await client.PutAsync($"/api/messages/{created.Id}", Wire.TextBody("streamed again"))).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/messages/{created.Id}")).EnsureSuccessStatusCode();

        var createdEvent = await events.NextAsync(About(created, "message.created"));
        var updatedEvent = await events.NextAsync(About(created, "message.updated"));
        var deletedEvent = await events.NextAsync(About(created, "message.deleted"));

        foreach (var e in new[] { createdEvent, updatedEvent, deletedEvent })
        {
            await Assert.That(e.Type).IsEqualTo(e.Event);
            await Assert.That(e.Id).IsNotNull();
            await Assert.That(e.Data.TryGetProperty("at", out _)).IsTrue();
        }

        await Assert.That(long.Parse(updatedEvent.Id!)).IsGreaterThan(long.Parse(createdEvent.Id!));
        await Assert.That(long.Parse(deletedEvent.Id!)).IsGreaterThan(long.Parse(updatedEvent.Id!));
        await Assert.That(createdEvent.Data.GetProperty("message").Deserialize<MessageBody>(Wire.JsonOptions)).IsEqualTo(created);
        await Assert.That(updatedEvent.Data.GetProperty("message").Deserialize<MessageBody>(Wire.JsonOptions))
            .IsEqualTo(created with { Text = "streamed again" });
        await Assert.That(deletedEvent.Data.TryGetProperty("message", out _)).IsFalse();
    }

    [Test]
    public async Task EventFilter_StreamsOnlyTheNamedEvents()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client, "?event=message.deleted,message.updated");
        await events.NextAsync(IsHeartbeat);

        var message = await client.CreateMessageAsync("filtered");
        (await client.DeleteAsync($"/api/messages/{message.Id}")).EnsureSuccessStatusCode();

        var received = await events.UntilAsync(About(message, "message.deleted"));

        await Assert.That(received.Where(e => !IsHeartbeat(e)).Select(e => e.Event).Distinct().Except(["message.deleted", "message.updated"]))
            .IsEmpty();
    }

    [Test]
    public async Task ExcludeFilter_DropsNamedEvents_AfterANamespaceInclude()
    {
        var client = target.CreateAuthenticatedClient();
        await using var events = await EventStream.OpenAsync(client, "?event=message.*&exclude_event=message.created,message.updated");
        await events.NextAsync(IsHeartbeat);

        var message = await client.CreateMessageAsync("excluded");
        (await client.PutAsync($"/api/messages/{message.Id}", Wire.TextBody("excluded again"))).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/messages/{message.Id}")).EnsureSuccessStatusCode();

        var received = await events.UntilAsync(About(message, "message.deleted"));

        await Assert.That(received.Where(e => !IsHeartbeat(e)).Select(e => e.Event).Distinct().ToList()).IsEquivalentTo(["message.deleted"]);
    }

    [Test]
    [Arguments("?event=message.exploded", "'event' has unknown event 'message.exploded'")]
    [Arguments("?exclude_event=heartbeat", "'exclude_event' has unknown event 'heartbeat'")]
    public async Task UnknownEventName_Is400(string query, string expected)
    {
        using var response = await EventStream.SendAsync(target.CreateAuthenticatedClient(), query);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var messages = await response.ProblemMessagesAsync();
        await Assert.That(messages).HasSingleItem();
        await Assert.That(messages[0]).StartsWith(expected);
    }

    [Test]
    public async Task MalformedLastEventId_Is400()
    {
        using var response = await EventStream.SendAsync(target.CreateAuthenticatedClient(), lastEventId: "not-an-id");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync())
            .IsEquivalentTo(["'Last-Event-ID' is not an event id of this stream: 'not-an-id'."]);
    }

    [Test]
    public async Task LastEventId_ReplaysWhatWasMissed()
    {
        var client = target.CreateAuthenticatedClient();
        MessageBody seen;
        string seenId;
        await using (var events = await EventStream.OpenAsync(client))
        {
            await events.NextAsync(IsHeartbeat);
            seen = await client.CreateMessageAsync("seen");
            seenId = (await events.NextAsync(About(seen, "message.created"))).Id!;
        }

        var missed = await client.CreateMessageAsync("missed");

        // Replayed events come before the connect heartbeat.
        await using var resumed = await EventStream.OpenAsync(client, lastEventId: seenId);
        var replayed = await resumed.UntilAsync(IsHeartbeat);

        await Assert.That(replayed.Any(About(missed, "message.created"))).IsTrue();
        await Assert.That(replayed.Any(About(seen, "message.created"))).IsFalse();
        await Assert.That(replayed.Where(e => !IsHeartbeat(e)).All(e => long.Parse(e.Id!) > long.Parse(seenId))).IsTrue();
    }

    [Test]
    public async Task WithoutLastEventId_NothingIsReplayed()
    {
        var client = target.CreateAuthenticatedClient();
        var earlier = await client.CreateMessageAsync("before connecting");

        await using var events = await EventStream.OpenAsync(client);
        var beforeHeartbeat = await events.UntilAsync(IsHeartbeat);

        await Assert.That(beforeHeartbeat).HasSingleItem();
        await Assert.That(beforeHeartbeat.Any(About(earlier, "message.created"))).IsFalse();
    }
}
