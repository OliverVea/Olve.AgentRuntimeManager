using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Olve.AgentRuntimeManager.Api;

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
