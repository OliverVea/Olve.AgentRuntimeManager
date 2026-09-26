using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Events;

/// <summary>
/// <c>GET /api/events</c>: replays the buffered events after <c>Last-Event-ID</c>, sends a
/// heartbeat, then streams live events, with a heartbeat every
/// <see cref="EventOptions.HeartbeatInterval"/>. The filter and the id are validated here, before
/// anything is streamed, so a bad request is a 400 rather than a broken stream.
/// </summary>
public sealed class StreamEventsHandler(EventBus bus, TimeProvider time, IOptions<EventOptions> options) : IEventsStreamHandler
{
    public Task<EventsStreamResponse> HandleAsync(EventsStreamRequest request, CancellationToken cancellationToken)
    {
        if (EventFilter.Parse(request.Event, request.ExcludeEvent).TryPickProblems(out var problems, out var filter))
        {
            return Task.FromResult<EventsStreamResponse>(new EventsStreamResponse.BadRequest(ArmErrors.Invalid([.. problems])));
        }

        long? after = null;
        if (request.LastEventId is { } lastEventId)
        {
            if (!long.TryParse(lastEventId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return Task.FromResult<EventsStreamResponse>(new EventsStreamResponse.BadRequest(ArmError.Create(
                    ArmErrors.InvalidRequest, $"'Last-Event-ID' is not an event id of this stream: '{lastEventId}'.")));
            }

            after = id;
        }

        return Task.FromResult<EventsStreamResponse>(new EventsStreamResponse.Ok(Stream(filter, after, cancellationToken)));
    }

    private async IAsyncEnumerable<ArmSseItem<ArmEvent>> Stream(
        EventFilter filter,
        long? after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var interval = options.Value.HeartbeatInterval;
        using var subscription = bus.Subscribe(after);

        foreach (var stored in subscription.Replay.Where(e => filter.Matches(e.Data.EventType)))
        {
            yield return Item(stored);
        }

        yield return Heartbeat();
        var nextHeartbeat = time.GetUtcNow() + interval;

        while (true)
        {
            var wait = nextHeartbeat - time.GetUtcNow();
            if (wait <= TimeSpan.Zero)
            {
                yield return Heartbeat();
                nextHeartbeat = time.GetUtcNow() + interval;
                continue;
            }

            bool open;
            using (var timeout = new CancellationTokenSource(wait, time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    open = await subscription.Live.WaitToReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue; // heartbeat due
                }
            }

            if (!open)
            {
                yield break; // fell too far behind: the client resumes with Last-Event-ID
            }

            while (subscription.Live.TryRead(out var stored))
            {
                if (filter.Matches(stored.Data.EventType))
                {
                    yield return Item(stored);
                }
            }
        }
    }

    private static ArmSseItem<ArmEvent> Item(StoredEvent stored) =>
        new(stored.Data, stored.Id.ToString(CultureInfo.InvariantCulture));

    private ArmSseItem<ArmEvent> Heartbeat() => new(new Heartbeat { At = time.GetUtcNow() });
}
