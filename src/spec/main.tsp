import "@typespec/http";
import "@typespec/streams";
import "@typespec/events";
import "@typespec/sse";

using Http;
using SSE;

/** ARM API contract. Source of truth — see docs/SPEC-FIRST.md. */
@service(#{ title: "Olve.AgentRuntimeManager" })
@useAuth(BearerAuth)
@route("/api")
namespace Arm;

// --- errors ----------------------------------------------------------------------------------
// Every error uses one envelope (docs/STANDARDS.md). Each operation declares the error statuses
// it can answer; its handler returns them as typed responses, so an undeclared one can't compile.

/** The body of every error response. */
model ErrorEnvelope {
  error: ErrorBody;
}

model ErrorBody {
  /** Stable, `SCREAMING_SNAKE_CASE` (`SESSION_NOT_FOUND`); `INVALID_REQUEST` for malformed or invalid input. */
  code: string;

  message: string;
  details: ErrorDetails;
}

model ErrorDetails {
  /** Every problem found, when there was more than one (e.g. several invalid fields). */
  problems?: ErrorProblem[];
}

model ErrorProblem {
  code: string;
  message: string;
}

@error
model BadRequest {
  @statusCode status: 400;
  @body error: ErrorEnvelope;
}

@error
model NotFound {
  @statusCode status: 404;
  @body error: ErrorEnvelope;
}

@error
model Conflict {
  @statusCode status: 409;
  @body error: ErrorEnvelope;
}

@error
model ServiceUnavailable {
  @statusCode status: 503;
  @body error: ErrorEnvelope;
}

/** A repeated key within 24 hours returns the original response instead of acting again. */
model IdempotencyKey {
  @header("Idempotency-Key") idempotencyKey?: string;
}

// --- auth config -----------------------------------------------------------------------------

/** Public OIDC settings the SPA needs to log in. */
model FrontendAuthConfig {
  authority: string | null;
  clientId: string | null;
  scopes: string;
}

@route("/auth-config")
interface AuthConfig {
  @useAuth(NoAuth)
  @get get(): FrontendAuthConfig;
}

// --- sessions --------------------------------------------------------------------------------

/**
 * `queued → working → completed`; `working ⇄ waiting` (waiting = pending approval);
 * `queued|working|waiting → killed`; `queued|working → failed`. Terminal: completed, killed, failed.
 */
enum SessionStatus {
  queued,
  working,
  waiting,
  completed,
  killed,
  failed,
}

/** Who ended a killed session. */
enum KillSource {
  user,
  system,
  timeout,
}

/** Everything a session is created with. All agent configuration is inline. */
model CreateSession {
  /** What the agent should do. */
  @minLength(1) prompt: string;

  /** The provider that runs the agent (default: the server's default provider). */
  provider?: string;

  `model`?: string;
  effort?: string;

  /** An additional system prompt. */
  systemPrompt?: string;

  /** Who started the session (e.g. `oribot`); searchable. */
  caller?: string;

  tags?: Record<string>;

  /** Environment for the agent. */
  env?: Record<string>;

  /**
   * Environment only passed to commands run through `approved_bash`. Write-only: never
   * returned, logged or put in events.
   */
  secretEnv?: Record<string>;

  /** Kill the session after this long (default: the server's background timeout). */
  @minValue(1) timeoutSeconds?: int32;

  approvalPolicy?: string;
  tools?: string[];
  skills?: string[];

  /** Whether the messages endpoint is enabled (default true). */
  messaging?: boolean;

  /** Auto-deny anything the policy doesn't allow, for CI and scripting (default false). */
  headless?: boolean;
}

/** A session: its configuration (defaults resolved, secrets omitted) and its lifecycle. */
model Session {
  @format("uuid") id: string;
  status: SessionStatus;

  /** Position in the queue (1 = next to start); only while queued. */
  queuePosition?: int32;

  prompt: string;
  provider: string;
  `model`?: string;
  effort?: string;
  systemPrompt?: string;
  caller?: string;
  tags: Record<string>;
  env: Record<string>;
  timeoutSeconds: int32;
  approvalPolicy?: string;
  tools: string[];
  skills: string[];
  messaging: boolean;
  headless: boolean;

  createdAt: utcDateTime;
  startedAt?: utcDateTime;
  endedAt?: utcDateTime;

  /** The provider's own id for the running agent, once started. */
  providerSessionId?: string;

  /** When completed. */
  exitCode?: int32;

  /** When completed: the agent's final summary. */
  summary?: string;

  /** When failed. */
  error?: string;

  /** When killed. */
  killReason?: string;

  killSource?: KillSource;
}

/** Filters for searching sessions; every field is optional, and empty matches everything. */
model SessionSearch {
  status?: SessionStatus;
  caller?: string;

  /** Sessions that have every one of these tags. */
  tags?: Record<string>;

  createdAfter?: utcDateTime;
  createdBefore?: utcDateTime;

  /** Case-insensitive substring of the prompt. */
  text?: string;

  /** Page size (default 20). */
  @minValue(1) @maxValue(100) limit?: int32;

  @minValue(0) offset?: int32;
}

/** One page of a session search, newest first. */
model SessionPage {
  items: Session[];

  /** Matches in total, across pages. */
  total: int32;

  limit: int32;
  offset: int32;
}

model KillSession {
  reason?: string;
}

@route("/sessions")
interface Sessions {
  /**
   * Creates a session. It starts right away (201) when a slot is free, else it is queued (202,
   * with `queuePosition`); a full queue is a 503.
   */
  @post create(...IdempotencyKey, @body body: CreateSession):
    | (CreatedResponse & { @body body: Session })
    | (AcceptedResponse & { @body body: Session })
    | BadRequest
    | UnauthorizedResponse
    | ServiceUnavailable;

  /** Searches sessions, newest first. */
  @route("/search")
  @post search(@body body: SessionSearch): SessionPage | BadRequest | UnauthorizedResponse;

  @get get(@path @format("uuid") id: string): Session | BadRequest | NotFound | UnauthorizedResponse;

  /** Kills a queued, working or waiting session; a session that already ended is a 409. */
  @route("/{id}/kill")
  @post kill(@path @format("uuid") id: string, @body body: KillSession):
    | Session
    | BadRequest
    | NotFound
    | Conflict
    | UnauthorizedResponse;

  /** Deletes a session that has ended; one that hasn't is a 409. */
  @delete delete(@path @format("uuid") id: string):
    | NoContentResponse
    | BadRequest
    | NotFound
    | Conflict
    | UnauthorizedResponse;
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

/** A session was created (queued or about to start). */
model SessionCreated {
  type: "session.created";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  session: Session;
}

/** A session is waiting for a slot. */
model SessionQueued {
  type: "session.queued";
  at: utcDateTime;
  @format("uuid") sessionId: string;

  /** 1 = next to start. */
  position: int32;
}

/** A session's agent started. */
model SessionStarted {
  type: "session.started";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  previous: SessionStatus;
  providerSessionId: string;
}

/** A session is waiting for an approval. */
model SessionWaiting {
  type: "session.waiting";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  previous: SessionStatus;
}

/** A waiting session is working again. */
model SessionResumed {
  type: "session.resumed";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  previous: SessionStatus;
}

/** A session's agent finished. */
model SessionCompleted {
  type: "session.completed";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  previous: SessionStatus;
  exitCode: int32;
  summary?: string;
}

/** A session's agent failed (or couldn't start). */
model SessionFailed {
  type: "session.failed";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  previous: SessionStatus;
  error: string;
}

/** A session was killed: by a user, the system, or its timeout. */
model SessionKilled {
  type: "session.killed";
  at: utcDateTime;
  @format("uuid") sessionId: string;
  previous: SessionStatus;
  reason?: string;
  source: KillSource;
}

/** The events `GET /api/events` streams; the variant name is the SSE event name. */
@TypeSpec.Events.events
union ArmEvent {
  @TypeSpec.Events.contentType("application/json") heartbeat: Heartbeat,
  @TypeSpec.Events.contentType("application/json") `session.created`: SessionCreated,
  @TypeSpec.Events.contentType("application/json") `session.queued`: SessionQueued,
  @TypeSpec.Events.contentType("application/json") `session.started`: SessionStarted,
  @TypeSpec.Events.contentType("application/json") `session.waiting`: SessionWaiting,
  @TypeSpec.Events.contentType("application/json") `session.resumed`: SessionResumed,
  @TypeSpec.Events.contentType("application/json") `session.completed`: SessionCompleted,
  @TypeSpec.Events.contentType("application/json") `session.failed`: SessionFailed,
  @TypeSpec.Events.contentType("application/json") `session.killed`: SessionKilled,
}

/** Any event's data, discriminated on `type` (the same payloads as `ArmEvent`). */
@discriminated(#{ envelope: "none", discriminatorPropertyName: "type" })
union ArmEventData {
  heartbeat: Heartbeat,
  `session.created`: SessionCreated,
  `session.queued`: SessionQueued,
  `session.started`: SessionStarted,
  `session.waiting`: SessionWaiting,
  `session.resumed`: SessionResumed,
  `session.completed`: SessionCompleted,
  `session.failed`: SessionFailed,
  `session.killed`: SessionKilled,
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
