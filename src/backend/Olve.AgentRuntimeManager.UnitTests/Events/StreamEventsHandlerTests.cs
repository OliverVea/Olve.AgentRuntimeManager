using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.Results.TUnit;

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

    private static MessageDeleted Deleted() => new() { At = Start, MessageId = Guid.NewGuid() };

    private async Task<IAsyncEnumerator<ArmSseItem<ArmEvent>>> Open(string[]? include = null, string[]? exclude = null, string? lastEventId = null)
    {
        var result = await _handler.HandleAsync(new EventsStreamRequest(include, exclude, lastEventId), CancellationToken.None);
        result.TryPickValue(out var stream);
        return stream!.GetAsyncEnumerator();
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
        var published = _bus.Publish(Deleted());

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
        var missedFrom = _bus.Publish(Deleted());
        var missed = _bus.Publish(Deleted());

        await using var stream = await Open(lastEventId: Id(missedFrom));

        var replayed = await Next(stream);
        await Assert.That(replayed.Id).IsEqualTo(Id(missed));
        await Assert.That((await Next(stream)).Data).IsTypeOf<Heartbeat>();
    }

    [Test]
    public async Task Filters_ApplyToReplayAndLiveEvents()
    {
        var before = _bus.Publish(Deleted());
        _bus.Publish(new MessageCreated { At = Start, MessageId = Guid.NewGuid(), Message = new Message { Id = Guid.NewGuid(), Text = "x" } });
        var replayedDelete = _bus.Publish(Deleted());

        await using var stream = await Open(include: ["message.*"], exclude: ["message.created"], lastEventId: Id(before));

        await Assert.That((await Next(stream)).Id).IsEqualTo(Id(replayedDelete));
        await Assert.That((await Next(stream)).Data).IsTypeOf<Heartbeat>();

        var pending = Next(stream);
        _bus.Publish(new MessageCreated { At = Start, MessageId = Guid.NewGuid(), Message = new Message { Id = Guid.NewGuid(), Text = "y" } });
        var liveDelete = _bus.Publish(Deleted());
        await Assert.That((await pending).Id).IsEqualTo(Id(liveDelete));
    }

    [Test]
    [Arguments("abc")]
    [Arguments("-1")]
    [Arguments("")]
    public async Task MalformedLastEventId_FailsBeforeStreaming(string lastEventId)
    {
        var result = await _handler.HandleAsync(new EventsStreamRequest(null, null, lastEventId), CancellationToken.None);

        await Assert.That(result).Failed();
        await Assert.That(_bus.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownFilterName_FailsBeforeStreaming()
    {
        var result = await _handler.HandleAsync(new EventsStreamRequest(["message.exploded"], null, null), CancellationToken.None);

        await Assert.That(result).Failed();
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
        var result = await _handler.HandleAsync(new EventsStreamRequest(null, null, null), CancellationToken.None);
        result.TryPickValue(out var events);
        var stream = events!.GetAsyncEnumerator(cancellation.Token);
        await Next(stream);

        var pending = stream.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();

        await Assert.That(async () => await pending.WaitAsync(Guard)).Throws<OperationCanceledException>();
        await stream.DisposeAsync();
        await Assert.That(_bus.SubscriberCount).IsEqualTo(0);
    }
}
