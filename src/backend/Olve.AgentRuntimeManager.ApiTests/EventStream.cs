using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>One received event: its SSE name, id (null when it has none) and parsed JSON data.</summary>
public sealed record ReceivedEvent(string Event, string? Id, JsonElement Data)
{
    public string Type => Data.GetProperty("type").GetString()!;

    public Guid? SessionId => Data.TryGetProperty("sessionId", out var id) ? id.GetGuid() : null;

    public override string ToString() => $"{Event}#{Id} {Data}";
}

/// <summary>
/// An open <c>GET /api/events</c> stream read as raw SSE (the BCL's <see cref="SseParser"/>).
/// Every read is bounded by <see cref="Timeout"/>, so a missing event fails the test instead of
/// hanging it. The target's store is shared (and other tests run concurrently), so tests look
/// for their own events with <see cref="NextAsync(Func{ReceivedEvent, bool})"/> rather than
/// assuming what comes next.
/// </summary>
public sealed class EventStream : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpResponseMessage _response;
    private readonly IAsyncEnumerator<SseItem<string>> _items;
    private readonly CancellationTokenSource _cancellation = new(Timeout);

    private EventStream(HttpResponseMessage response, Stream body)
    {
        _response = response;
        _items = SseParser.Create(body).EnumerateAsync(_cancellation.Token).GetAsyncEnumerator(_cancellation.Token);
    }

    /// <summary>Opens the stream; fails unless the server answers 200 <c>text/event-stream</c>.</summary>
    public static async Task<EventStream> OpenAsync(HttpClient client, string query = "", string? lastEventId = null)
    {
        var response = await SendAsync(client, query, lastEventId);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            var body = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new InvalidOperationException($"GET /api/events{query}: {(int)response.StatusCode} {response.Content.Headers.ContentType} {body}");
        }

        return new EventStream(response, await response.Content.ReadAsStreamAsync());
    }

    /// <summary>The raw response, for requests that are expected to fail.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string query = "", string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events" + query);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        using var timeout = new CancellationTokenSource(Timeout);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    public async Task<ReceivedEvent> NextAsync()
    {
        if (!await _items.MoveNextAsync())
        {
            throw new InvalidOperationException("The event stream ended.");
        }

        var item = _items.Current;
        using var data = JsonDocument.Parse(item.Data);
        return new ReceivedEvent(item.EventType, item.EventId is { Length: > 0 } id ? id : null, data.RootElement.Clone());
    }

    /// <summary>The next event matching <paramref name="predicate"/>; the ones before it are skipped.</summary>
    public async Task<ReceivedEvent> NextAsync(Func<ReceivedEvent, bool> predicate)
    {
        while (true)
        {
            var received = await NextAsync();
            if (predicate(received))
            {
                return received;
            }
        }
    }

    /// <summary>Every event up to and including the next one matching <paramref name="predicate"/>.</summary>
    public async Task<List<ReceivedEvent>> UntilAsync(Func<ReceivedEvent, bool> predicate)
    {
        var received = new List<ReceivedEvent>();
        while (true)
        {
            received.Add(await NextAsync());
            if (predicate(received[^1]))
            {
                return received;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await _items.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected: the read was cancelled.
        }

        _response.Dispose();
        _cancellation.Dispose();
    }
}
