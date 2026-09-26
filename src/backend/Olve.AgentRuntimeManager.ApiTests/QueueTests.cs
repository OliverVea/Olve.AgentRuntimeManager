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
        var queued = (await second.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;
        await Assert.That(queued.Status).IsEqualTo("queued");
        await Assert.That(queued.QueuePosition).IsEqualTo(1);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await third.ErrorAsync()).Code).IsEqualTo("QUEUE_FULL");

        var running = (await first.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;
        (await client.KillSessionAsync(running.Id)).EnsureSuccessStatusCode();

        var started = await client.GetSessionAsync(queued.Id);
        await Assert.That(started.Status).IsEqualTo("working");
        await Assert.That(started.QueuePosition).IsNull();
        (await client.KillSessionAsync(queued.Id)).EnsureSuccessStatusCode();
    }

    [Test]
    public async Task KillingAQueuedSession_RemovesItFromTheQueue()
    {
        var client = Client();
        var running = await client.CreateHangingSessionAsync();
        var queued = await client.CreateHangingSessionAsync();

        using var kill = await client.KillSessionAsync(queued.Id);
        var killed = (await kill.Content.ReadFromJsonAsync<SessionBody>(Wire.JsonOptions))!;

        await Assert.That(queued.Status).IsEqualTo("queued");
        await Assert.That(killed.Status).IsEqualTo("killed");
        await Assert.That(killed.QueuePosition).IsNull();
        await Assert.That(killed.StartedAt).IsNull();
        (await client.KillSessionAsync(running.Id)).EnsureSuccessStatusCode();
    }
}
