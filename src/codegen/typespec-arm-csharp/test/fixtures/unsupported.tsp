// Shapes the emitter can't type must be reported, not silently mistyped: an untagged union, an
// exploded array parameter, and an SSE stream whose event's `type` disagrees with its name.
import "@typespec/http";
import "@typespec/streams";
import "@typespec/events";
import "@typespec/sse";

using Http;
using SSE;

@service
namespace Unsupported;

model A {
  x: string | int32;
}

@get op get(): A;

@route("/tags") @get op tags(@query(#{ explode: true }) tag?: string[]): A;

model Tick {
  type: "tock";
}

@TypeSpec.Events.events
union Ticks {
  @TypeSpec.Events.contentType("application/json") tick: Tick,
}

@route("/ticks") @get op ticks(): SSEStream<Ticks>;
