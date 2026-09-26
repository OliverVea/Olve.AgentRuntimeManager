using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;

namespace Olve.AgentRuntimeManager.UnitTests.Events;

public class StreamEventsHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly EventBus _bus;
    private readonly StreamEventsHandler _handler;

    public StreamEventsHandlerTests()
    {
        var options = Options.Create(new EventOptions { HeartbeatInterval = Interval });
        _bus = new EventBus(_time, options);
        _handler = new StreamEventsHandler(_bus, _time, options);
    }

    private static SessionQueued Queued() => new() { At = Start, SessionId = Guid.NewGuid(), Position = 1 };

    private async Task<IAsyncEnumerator<ArmSseItem<ArmEvent>>> Open(string[]? include = null, string[]? exclude = null, string? lastEventId = null)
    {
        var response = await _handler.HandleAsync(new EventsStreamRequest(include, exclude, lastEventId), CancellationToken.None);
        return ((EventsStreamResponse.Ok)response).Events.GetAsyncEnumerator();
    }

    private static async Task<ArmSseItem<ArmEvent>> Next(IAsyncEnumerator<ArmSseItem<ArmEvent>> stream)
    {
        var moved = await stream.MoveNextAsync().AsTask().WaitAsync(Guard);
        return moved ? stream.Current : throw new InvalidOperationException("The stream ended.");
    }

    private static string Id(StoredEvent stored) => stored.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    public async Task Connect_SendsAHeartbeatWithoutAnId()
    {
        await using var stream = await Open();

        var first = await Next(stream);

        await Assert.That(first.Data).IsEqualTo(new Heartbeat { At = Start });
        await Assert.That(first.Id).IsNull();
    }

    [Test]
    public async Task LiveEvents_ArriveWithTheirIds()
    {
        await using var stream = await Open();
        await Next(stream); // heartbeat

        var pending = Next(stream);
        var published = _bus.Publish(Queued());

        var item = await pending;
        await Assert.That(item.Data).IsEqualTo(published.Data);
        await Assert.That(item.Id).IsEqualTo(Id(published));
    }

    [Test]
    public async Task Heartbeats_RepeatEveryInterval()
    {
        await using var stream = await Open();
        await Next(stream);

        var pending = Next(stream);
        _time.Advance(Interval);
        var heartbeat = await pending;

        await Assert.That(heartbeat.Data).IsEqualTo(new Heartbeat { At = Start + Interval });
        await Assert.That(heartbeat.Id).IsNull();
    }

    [Test]
    public async Task LastEventId_ReplaysLaterEvents_BeforeTheHeartbeat()
    {
        var missedFrom = _bus.Publish(Queued());
        var missed = _bus.Publish(Queued());

        await using var stream = await Open(lastEventId: Id(missedFrom));

        var replayed = await Next(stream);
        await Assert.That(replayed.Id).IsEqualTo(Id(missed));
        await Assert.That((await Next(stream)).Data).IsTypeOf<Heartbeat>();
    }

    [Test]
    public async Task Filters_ApplyToReplayAndLiveEvents()
    {
        var before = _bus.Publish(Queued());
        _bus.Publish(new SessionStarted { At = Start, SessionId = Guid.NewGuid(), Previous = SessionStatus.Queued, ProviderSessionId = "p" });
        var replayedQueued = _bus.Publish(Queued());

        await using var stream = await Open(include: ["session.*"], exclude: ["session.started"], lastEventId: Id(before));

        await Assert.That((await Next(stream)).Id).IsEqualTo(Id(replayedQueued));
        await Assert.That((await Next(stream)).Data).IsTypeOf<Heartbeat>();

        var pending = Next(stream);
        _bus.Publish(new SessionStarted { At = Start, SessionId = Guid.NewGuid(), Previous = SessionStatus.Queued, ProviderSessionId = "p" });
        var liveQueued = _bus.Publish(Queued());
        await Assert.That((await pending).Id).IsEqualTo(Id(liveQueued));
    }

    [Test]
    [Arguments("abc")]
    [Arguments("-1")]
    [Arguments("")]
    public async Task MalformedLastEventId_FailsBeforeStreaming(string lastEventId)
    {
        var result = await _handler.HandleAsync(new EventsStreamRequest(null, null, lastEventId), CancellationToken.None);

        await Assert.That(result).IsTypeOf<EventsStreamResponse.BadRequest>();
        await Assert.That(_bus.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownFilterName_FailsBeforeStreaming()
    {
        var result = await _handler.HandleAsync(new EventsStreamRequest(["session.exploded"], null, null), CancellationToken.None);

        await Assert.That(result).IsTypeOf<EventsStreamResponse.BadRequest>();
    }

    [Test]
    public async Task ClosingTheStream_Unsubscribes()
    {
        var stream = await Open();
        await Next(stream);
        await Assert.That(_bus.SubscriberCount).IsEqualTo(1);

        await stream.DisposeAsync();

        await Assert.That(_bus.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task Cancellation_EndsAPendingRead_AndUnsubscribes()
    {
        using var cancellation = new CancellationTokenSource();
        var response = await _handler.HandleAsync(new EventsStreamRequest(null, null, null), CancellationToken.None);
        var stream = ((EventsStreamResponse.Ok)response).Events.GetAsyncEnumerator(cancellation.Token);
        await Next(stream);

        var pending = stream.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();

        await Assert.That(async () => await pending.WaitAsync(Guard)).Throws<OperationCanceledException>();
        await stream.DisposeAsync();
        await Assert.That(_bus.SubscriberCount).IsEqualTo(0);
    }
}
