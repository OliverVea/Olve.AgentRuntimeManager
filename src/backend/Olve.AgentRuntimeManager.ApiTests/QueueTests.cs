using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// The queue in front of the slots: a host with one slot and room for one queued session. In
/// process only (it needs its own configuration); the tests share that host, so they run in order.
/// </summary>
[ClassDataSource<SmallQueueFactory>(Shared = SharedType.PerClass)]
[NotInParallel]
public class QueueTests(SmallQueueFactory factory)
{
    private HttpClient Client()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only.");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Tokens.Mint());
        return client;
    }

    [Test]
    public async Task FullSlots_Queue_ThenAFullQueue_Is503_AndAFreedSlotStartsTheNext()
    {
        var client = Client();

        using var first = await client.PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("fake:hang")));
        using var second = await client.PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("fake:hang")));
        using var third = await client.PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("fake:hang")));

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var queued = await client.GetSessionAsync((await second.Content.ReadFromJsonAsync<CreatedSessionBody>(Wire.JsonOptions))!.Id);
        await Assert.That(queued.Status).IsEqualTo("queued");
        await Assert.That(queued.QueuePosition).IsEqualTo(1);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await third.ErrorAsync()).Code).IsEqualTo("QUEUE_FULL");

        var running = (await first.Content.ReadFromJsonAsync<CreatedSessionBody>(Wire.JsonOptions))!;
        (await client.KillSessionAsync(running.Id)).EnsureSuccessStatusCode();

        var started = await client.GetSessionAsync(queued.Id);
        await Assert.That(started.Status).IsEqualTo("working");
        await Assert.That(started.QueuePosition).IsNull();
        (await client.KillSessionAsync(queued.Id)).EnsureSuccessStatusCode();
    }

    [Test]
    public async Task KillingAQueuedSession_CancelsIt_AndRemovesItFromTheQueue()
    {
        var client = Client();
        var running = await client.CreateHangingSessionAsync();
        var queued = await client.CreateHangingSessionAsync();

        using var kill = await client.KillSessionAsync(queued.Id);
        var killed = (await kill.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;

        await Assert.That(queued.Status).IsEqualTo("queued");
        await Assert.That(killed.Status).IsEqualTo("cancelled");
        await Assert.That(killed.KillCaller).IsEqualTo("api-tests");
        await Assert.That(killed.QueuePosition).IsNull();
        await Assert.That(killed.StartedAt).IsNull();
        (await client.KillSessionAsync(running.Id)).EnsureSuccessStatusCode();
    }

    [Test]
    public async Task MessagesToAQueuedSession_AreHeld_AndGivenToItsAgent_AfterThePrompt()
    {
        var client = Client();
        var running = await client.CreateHangingSessionAsync();
        var queued = await client.CreateHangingSessionAsync();

        var first = await client.SentMessageAsync(queued.Id, "One. fake:say=First");
        var second = await client.SentMessageAsync(queued.Id, "Two. fake:say=Second");
        var before = (await client.GetFromJsonAsync<ConversationBody>($"/api/sessions/{queued.Id}/conversation", Wire.JsonOptions))!;
        (await client.KillSessionAsync(running.Id)).EnsureSuccessStatusCode();
        await client.WaitForSessionAsync(queued.Id, s => s.Status == "working");
        var after = (await client.GetFromJsonAsync<ConversationBody>($"/api/sessions/{queued.Id}/conversation", Wire.JsonOptions))!;
        (await client.KillSessionAsync(queued.Id)).EnsureSuccessStatusCode();

        await Assert.That((first, second)).IsEqualTo(("pending", "pending"));
        await Assert.That(before.Entries).IsEmpty();
        await Assert.That(after.Entries.Select(e => (e.Kind, e.Text ?? ""))).IsEquivalentTo(
            [("prompt", "Wait. fake:hang"), ("prompt", "One. fake:say=First"), ("text", "First"), ("prompt", "Two. fake:say=Second"), ("text", "Second")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task MessageToASessionCancelledBeforeItStarted_Is409()
    {
        var client = Client();
        var running = await client.CreateHangingSessionAsync();
        var queued = await client.CreateHangingSessionAsync();
        (await client.KillSessionAsync(queued.Id)).EnsureSuccessStatusCode();

        using var response = await client.SendMessageAsync(queued.Id, "hello?");
        (await client.KillSessionAsync(running.Id)).EnsureSuccessStatusCode();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SESSION_NEVER_STARTED");
        await Assert.That((await client.GetSessionAsync(queued.Id)).Status).IsEqualTo("cancelled");
    }
}
