using System.Diagnostics.CodeAnalysis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// An event of a generated SSE event union (an <c>@events</c> union in the contract). The event
/// name on the wire is its <see cref="EventType"/>, which is also the <c>type</c> of its data.
/// </summary>
public interface IArmEvent
{
    /// <summary>The SSE event name, e.g. <c>message.created</c>.</summary>
    string EventType { get; }
}

/// <summary>
/// One event an SSE operation's handler yields: its data and its SSE id (<c>null</c> sends it
/// without one, e.g. heartbeats, so clients' <c>Last-Event-ID</c> never points at it).
/// </summary>
public readonly record struct ArmSseItem<T>(T Data, string? Id = null);

/// <summary>Query-string helpers the generated binding uses.</summary>
public static class ArmQuery
{
    /// <summary>
    /// An <c>explode: false</c> list (<c>?event=a,b</c>): comma-separated, entries trimmed, empty
    /// entries dropped. Absent stays null.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static IReadOnlyList<string>? List(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Writes a handler's events as <c>text/event-stream</c> (<see cref="TypedResults.ServerSentEvents{T}(IAsyncEnumerable{SseItem{T}})"/>):
/// each event's name is its <see cref="IArmEvent.EventType"/>, its id the item's, and its data the
/// JSON of the event serialized as the union (so the <c>type</c> discriminator is always written).
/// The stream ends quietly when the client disconnects or the app stops, so open streams never
/// hold up shutdown.
/// </summary>
public sealed class ArmServerSentEventsResult<T>(IAsyncEnumerable<ArmSseItem<T>> events) : IResult
    where T : IArmEvent
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, stopping);
        httpContext.Response.RegisterForDispose(cancellation);
        // Proxies (nginx-style) must pass events through as they come, not buffer the response.
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        return TypedResults.ServerSentEvents(Frames(events, typeInfo, cancellation.Token)).ExecuteAsync(httpContext);
    }

    private static async IAsyncEnumerable<SseItem<string>> Frames(
        IAsyncEnumerable<ArmSseItem<T>> events,
        JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = events.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool next;
            try
            {
                next = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (!next)
            {
                yield break;
            }

            var item = enumerator.Current;
            yield return new SseItem<string>(JsonSerializer.Serialize(item.Data, typeInfo), item.Data.EventType)
            {
                EventId = item.Id,
            };
        }
    }
}
