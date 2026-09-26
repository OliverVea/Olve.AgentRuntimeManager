import "@typespec/http";
import "@typespec/streams";
import "@typespec/events";
import "@typespec/sse";

using Http;
using SSE;

namespace Arm;

/** What every event carries: its name (`type`, also the SSE event name) and when it happened. */
model Event<Type extends string> {
  type: Type;
  at: utcDateTime;
}

/** Keeps the connection alive: sent on connect and then every 30s. No event id; never stored or replayed. */
model Heartbeat {
  ...Event<"heartbeat">;
}

/** The events `GET /api/events` streams; the variant name is the SSE event name. */
@TypeSpec.Events.events
union ArmEvent {
  @TypeSpec.Events.contentType("application/json") heartbeat: Heartbeat,
  @TypeSpec.Events.contentType("application/json") `session.created`: SessionCreated,
  @TypeSpec.Events.contentType("application/json") `session.queued`: SessionQueued,
  @TypeSpec.Events.contentType("application/json") `session.started`: SessionStarted,
  @TypeSpec.Events.contentType("application/json") `session.completed`: SessionCompleted,
  @TypeSpec.Events.contentType("application/json") `session.failed`: SessionFailed,
  @TypeSpec.Events.contentType("application/json") `session.cancelled`: SessionCancelled,
  @TypeSpec.Events.contentType("application/json") `session.killed`: SessionKilled,
  @TypeSpec.Events.contentType("application/json") `provider.health`: ProviderHealthChanged,
}

/** Any event's data, discriminated on `type` (the same payloads as `ArmEvent`). */
@discriminated(#{ envelope: "none", discriminatorPropertyName: "type" })
union ArmEventData {
  heartbeat: Heartbeat,
  `session.created`: SessionCreated,
  `session.queued`: SessionQueued,
  `session.started`: SessionStarted,
  `session.completed`: SessionCompleted,
  `session.failed`: SessionFailed,
  `session.cancelled`: SessionCancelled,
  `session.killed`: SessionKilled,
  `provider.health`: ProviderHealthChanged,
}

model EventFilter {
  /**
   * Only these events (comma list). A trailing `.*` matches a namespace (`session.*`).
   * Unknown names are a 400. Heartbeats are always sent and can't be filtered.
   */
  @query(#{ explode: false }) event?: string[];

  /** All events except these (comma list, same names as `event`); applied after `event`. */
  @query(#{ name: "exclude_event", explode: false }) excludeEvent?: string[];

  /** Replay the retained events after this id (the last one received), then continue live. */
  @header("Last-Event-ID") lastEventId?: string;
}

@route("/events")
interface Events {
  /**
   * Streams events as server-sent events. Every event but `heartbeat` has an id; reconnect with
   * `Last-Event-ID` to replay what was missed (from an in-memory buffer of recent events).
   */
  @get stream(...EventFilter): SSEStream<ArmEvent> | BadRequest | UnauthorizedResponse;
}
