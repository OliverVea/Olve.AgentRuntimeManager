using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;

namespace Olve.AgentRuntimeManager.UnitTests.Events;

public class EventBusTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static EventBus Bus(int capacity = 1000, DateTimeOffset? start = null) =>
        new(new FakeTimeProvider(start ?? Start), Options.Create(new EventOptions { ReplayCapacity = capacity }));

    private static SessionQueued Queued() => new() { At = Start, SessionId = Guid.NewGuid(), Position = 1 };

    private static List<StoredEvent> Drain(EventBus.Subscription subscription)
    {
        var events = new List<StoredEvent>();
        while (subscription.Live.TryRead(out var stored))
        {
            events.Add(stored);
        }

        return events;
    }

    [Test]
    public async Task Publish_AssignsIncreasingIds()
    {
        var bus = Bus();

        var ids = Enumerable.Range(0, 5).Select(_ => bus.Publish(Queued()).Id).ToList();

        await Assert.That(ids).IsInOrder();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(5);
    }

    [Test]
    public async Task Ids_StartAfterEveryIdOfAnEarlierRun()
    {
        var earlier = Bus(start: Start);
        var last = Enumerable.Range(0, 100).Select(_ => earlier.Publish(Queued()).Id).Last();

        var restarted = Bus(start: Start.AddSeconds(1));

        await Assert.That(restarted.Publish(Queued()).Id).IsGreaterThan(last);
    }

    [Test]
    public async Task Heartbeats_AreNeverPublished()
    {
        var bus = Bus();

        await Assert.That(() => bus.Publish(new Heartbeat { At = Start })).Throws<ArgumentException>();
    }

    [Test]
    public async Task Subscribe_WithoutAnId_GetsOnlyLiveEvents()
    {
        var bus = Bus();
        bus.Publish(Queued());

        using var subscription = bus.Subscribe();
        var live = bus.Publish(Queued());

        await Assert.That(subscription.Replay).IsEmpty();
        await Assert.That(Drain(subscription)).IsEquivalentTo([live]);
    }

    [Test]
    public async Task Subscribe_AfterAnId_ReplaysLaterEventsThenLive()
    {
        var bus = Bus();
        var first = bus.Publish(Queued());
        var second = bus.Publish(Queued());
        var third = bus.Publish(Queued());

        using var subscription = bus.Subscribe(first.Id);
        var live = bus.Publish(Queued());

        await Assert.That(subscription.Replay).IsEquivalentTo([second, third]);
        await Assert.That(Drain(subscription)).IsEquivalentTo([live]);
    }

    [Test]
    public async Task Subscribe_AfterAnIdOlderThanTheBuffer_ReplaysTheWholeBuffer()
    {
        var bus = Bus(capacity: 2);
        var evicted = bus.Publish(Queued());
        var kept = new[] { bus.Publish(Queued()), bus.Publish(Queued()) };

        using var subscription = bus.Subscribe(evicted.Id - 1);

        await Assert.That(subscription.Replay).IsEquivalentTo(kept);
    }

    [Test]
    public async Task Subscribe_AfterTheNewestId_ReplaysNothing()
    {
        var bus = Bus();
        var newest = bus.Publish(Queued());

        using var subscription = bus.Subscribe(newest.Id);

        await Assert.That(subscription.Replay).IsEmpty();
    }

    [Test]
    public async Task Dispose_Unsubscribes()
    {
        var bus = Bus();
        var subscription = bus.Subscribe();
        await Assert.That(bus.SubscriberCount).IsEqualTo(1);

        subscription.Dispose();
        bus.Publish(Queued());

        await Assert.That(bus.SubscriberCount).IsEqualTo(0);
        await Assert.That(subscription.Live.Completion.IsCompleted).IsTrue();
    }

    [Test]
    public async Task ASubscriberThatFallsBehind_IsDisconnected_WithoutBlockingPublish()
    {
        var bus = Bus(capacity: 2);
        using var slow = bus.Subscribe();

        var published = Enumerable.Range(0, 3).Select(_ => bus.Publish(Queued())).ToList();

        await Assert.That(bus.SubscriberCount).IsEqualTo(0);
        await Assert.That(Drain(slow)).IsEquivalentTo(published.Take(2));
        await Assert.That(slow.Live.Completion.IsCompleted).IsTrue();
    }
}
