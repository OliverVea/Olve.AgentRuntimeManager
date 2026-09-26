import "@typespec/http";
import "@typespec/streams";
import "@typespec/events";
import "@typespec/sse";

using Http;
using SSE;

/** ARM API contract. Source of truth — see docs/SPEC-FIRST.md. */
@service(#{ title: "Olve.AgentRuntimeManager" })
@route("/api")
namespace Arm;

/** A message — the scaffold's example entity, used to prove the toolchain (M1). */
model Message {
  @visibility(Lifecycle.Read) @format("uuid") id: string;
  @maxLength(280) text: string;
}

model Pagination {
  page?: int32;
  pageSize?: int32;
  offset?: int32;
}

model PageOfMessage {
  items: Message[];
  pageNumber: int32;
  pageSize: int32;
  totalCount: int32;
  totalPages?: int32;
  hasNextPage?: boolean;
  /** Null on the last page. */
  next?: Pagination | null;
}

/**
 * Current error shape (Olve.MinimalApi result mapping).
 * Replaced by the SPEC error envelope `{ error: { code, message, details } }` in M4 (A2).
 */
model ResultProblem {
  message: string;
  tags: string[] | null;
  severity: int32;
  source: string | null;
  exceptionSummary: string | null;
}

@error
model BadRequest {
  @statusCode status: 400;
  @body problems: ResultProblem[];
}

@error
model NotFound {
  @statusCode status: 404;
  @body problems: ResultProblem[];
}

/** Public OIDC settings the SPA needs to log in. */
model FrontendAuthConfig {
  authority: string | null;
  clientId: string | null;
  scopes: string;
}

@route("/messages")
interface Messages {
  /** List messages (1-based pages); out-of-range page/pageSize values are clamped. */
  @get list(@query page?: int32, @query pageSize?: int32): PageOfMessage;

  @post create(@body body: Message):
    | Message
    | BadRequest
    | UnauthorizedResponse;

  /** Get one message by id (anonymous, like `list`). */
  @get get(@path id: string): Message | NotFound;

  /**
   * A missing id is currently a 400 (Olve.MinimalApi maps every failed Result to 400), unlike
   * `get`'s 404; the SPEC error envelope in M4 settles not-found handling for all operations.
   */
  @put update(@path id: string, @body body: Message):
    | Message
    | BadRequest
    | UnauthorizedResponse;

  /** A missing id is currently a 400 — see `update`. */
  @delete delete(@path id: string): OkResponse | BadRequest | UnauthorizedResponse;
}

@route("/auth-config")
interface AuthConfig {
  @get get(): FrontendAuthConfig;
}

// --- events (SSE) ----------------------------------------------------------------------------
// Every event's JSON data carries `type` (the SSE event name), `at` and its subject id
// (docs/STANDARDS.md). Each payload is its own model, used twice: in the `@events` union the SSE
// stream is described by, and in the discriminated `ArmEventData` union that gives clients a
// narrowable type per event (Hey API types stream items as `unknown`).

/** Keeps the connection alive: sent on connect and then every 30s. No event id; never stored or replayed. */
model Heartbeat {
  type: "heartbeat";
  at: utcDateTime;
}

/** A message was created. */
model MessageCreated {
  type: "message.created";
  at: utcDateTime;
  @format("uuid") messageId: string;
  message: Message;
}

/** A message's text was replaced. */
model MessageUpdated {
  type: "message.updated";
  at: utcDateTime;
  @format("uuid") messageId: string;
  message: Message;
}

/** A message was deleted. */
model MessageDeleted {
  type: "message.deleted";
  at: utcDateTime;
  @format("uuid") messageId: string;
}

/** The events `GET /api/events` streams; the variant name is the SSE event name. */
@TypeSpec.Events.events
union ArmEvent {
  @TypeSpec.Events.contentType("application/json") heartbeat: Heartbeat,
  @TypeSpec.Events.contentType("application/json") `message.created`: MessageCreated,
  @TypeSpec.Events.contentType("application/json") `message.updated`: MessageUpdated,
  @TypeSpec.Events.contentType("application/json") `message.deleted`: MessageDeleted,
}

/** Any event's data, discriminated on `type` (the same payloads as `ArmEvent`). */
@discriminated(#{ envelope: "none", discriminatorPropertyName: "type" })
union ArmEventData {
  heartbeat: Heartbeat,
  `message.created`: MessageCreated,
  `message.updated`: MessageUpdated,
  `message.deleted`: MessageDeleted,
}

model EventFilter {
  /**
   * Only these events (comma list). A trailing `.*` matches a namespace (`message.*`).
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
