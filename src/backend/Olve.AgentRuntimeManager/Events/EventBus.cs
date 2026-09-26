using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Events;

/// <summary>An event as the bus stores it: its SSE id and its data.</summary>
public sealed record StoredEvent(long Id, ArmEvent Data);

/// <summary>
/// The in-process event bus behind <c>GET /api/events</c>. <see cref="Publish"/> gives each event
/// the next id and keeps the most recent <see cref="EventOptions.ReplayCapacity"/> in a replay
/// buffer; <see cref="Subscribe"/> hands a subscriber the buffered events after a given id plus
/// every later event, with no gap or duplicate at the seam (both happen under one lock).
/// Heartbeats never pass through the bus: they are per-connection and have no id.
/// </summary>
/// <remarks>
/// Ids are monotonic and, because they start at the process's start time in microseconds, keep
/// increasing across restarts (assuming fewer than a million events a second on average): a
/// <c>Last-Event-ID</c> from before a restart is older than every new event, so the client
/// replays whatever the new process has buffered instead of silently skipping ids it reuses.
/// The buffer itself is in memory; replay across restarts (SPEC §Events) needs a persistent log.
/// </remarks>
public sealed class EventBus
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly Queue<StoredEvent> _buffer = new();
    private readonly List<Channel<StoredEvent>> _subscribers = [];
    private long _lastId;

    public EventBus(TimeProvider time, IOptions<EventOptions> options)
    {
        _time = time;
        _capacity = Math.Max(1, options.Value.ReplayCapacity);
        _lastId = time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;
    }

    /// <summary>The current time, for events' <c>at</c>.</summary>
    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Open subscriptions (for tests and diagnostics).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// Stores and fans out an event. Never blocks: a subscriber too slow to keep up (its queue
    /// full) is disconnected instead, and resumes from the replay buffer with <c>Last-Event-ID</c>.
    /// </summary>
    public StoredEvent Publish(ArmEvent data)
    {
        if (data is Heartbeat)
        {
            throw new ArgumentException("Heartbeats are per connection and never published.", nameof(data));
        }

        lock (_gate)
        {
            var stored = new StoredEvent(++_lastId, data);
            _buffer.Enqueue(stored);
            while (_buffer.Count > _capacity)
            {
                _buffer.Dequeue();
            }

            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(stored))
                {
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }

            return stored;
        }
    }

    /// <summary>
    /// Subscribes to every event after <paramref name="afterId"/> (buffered ones first, then live);
    /// without an id, only to live events. Dispose to unsubscribe.
    /// </summary>
    public Subscription Subscribe(long? afterId = null)
    {
        var channel = Channel.CreateBounded<StoredEvent>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full: see Publish
        });

        lock (_gate)
        {
            IReadOnlyList<StoredEvent> replay = afterId is { } after ? [.. _buffer.Where(e => e.Id > after)] : [];
            _subscribers.Add(channel);
            return new Subscription(this, channel, replay);
        }
    }

    private void Unsubscribe(Channel<StoredEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    /// <summary>
    /// One subscriber: the <see cref="Replay"/> of buffered events, then <see cref="Live"/>, which
    /// completes when the subscriber falls too far behind.
    /// </summary>
    public sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;
        private readonly Channel<StoredEvent> _channel;

        internal Subscription(EventBus bus, Channel<StoredEvent> channel, IReadOnlyList<StoredEvent> replay)
        {
            _bus = bus;
            _channel = channel;
            Replay = replay;
        }

        public IReadOnlyList<StoredEvent> Replay { get; }

        public ChannelReader<StoredEvent> Live => _channel.Reader;

        public void Dispose() => _bus.Unsubscribe(_channel);
    }
}
