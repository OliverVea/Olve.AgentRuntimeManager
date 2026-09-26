import "@typespec/http";

using Http;

namespace Arm;

/**
 * `queued → working → completed`; `queued → cancelled` (withdrawn before it started);
 * `working → killed` (stopped while running); `queued|working → failed`.
 * Terminal: completed, cancelled, killed, failed.
 */
enum SessionStatus {
  queued,
  working,
  completed,
  cancelled,
  killed,
  failed,
}

/** Who ended a cancelled or killed session. */
enum KillSource {
  user,
  system,
  timeout,
}

/** What a session is created with. */
model CreateSession {
  /** The task: the first user message the agent receives. Must not be blank. */
  @minLength(1) prompt: string;

  /** The provider that runs the agent: `claude` (Claude Code), or `fake` (no LLM, for tests; not in prod). An unknown name is a 400 `UNKNOWN_PROVIDER`. */
  @minLength(1) provider: string;

  /** The model, in the provider's own naming, passed to it as is. The `fake` provider ignores it. */
  @minLength(1) `model`: string;

  /** Who started the session, e.g. an integration's name. Free text, for searching. */
  @minLength(1) caller: string;

  /**
   * Seconds the session may run once started (time queued doesn't count); then it is killed
   * with source `timeout`. Unset: no timeout.
   */
  @minValue(1) timeoutSeconds?: int32;
}

/** A session: what it was created with (defaults resolved) and its lifecycle. */
model Session {
  @format("uuid") id: string;
  status: SessionStatus;

  /** Set when status is `queued`: 1 = next to start. */
  queuePosition?: int32;

  prompt: string;
  provider: string;
  `model`: string;
  caller: string;

  /** Set when the session has a timeout. */
  timeoutSeconds?: int32;

  createdAt: utcDateTime;

  /** Set once the agent started: when status is `working`, `completed` or `killed`, and `failed` after starting. */
  startedAt?: utcDateTime;

  /** Set when status is `completed`, `cancelled`, `killed` or `failed`. */
  endedAt?: utcDateTime;

  /** The provider's own id for the agent. Set whenever `startedAt` is. */
  providerSessionId?: string;

  /** Set when status is `completed`. */
  exitCode?: int32;

  /** Set when status is `failed`. */
  error?: string;

  /** Set when status is `cancelled` or `killed` and a reason was given. */
  killReason?: string;

  /** Set when status is `cancelled` or `killed`. */
  killSource?: KillSource;

  /** The `caller` of the kill request. Set when status is `cancelled` or `killed` and `killSource` is `user`. */
  killCaller?: string;
}

/** Filters for searching sessions; every field is optional, and empty matches everything. */
model SessionSearch {
  /** Matches any of these statuses (all when left out). */
  @minItems(1) status?: SessionStatus[];
  caller?: string;
  createdAfter?: utcDateTime;
  createdBefore?: utcDateTime;

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

/** The session an operation is about. */
model SessionId {
  @path @format("uuid") id: string;
}

model KillSession {
  /** Who is killing the session, e.g. an integration's name. Free text, like the session's `caller`. */
  @minLength(1) caller: string;

  reason?: string;
}

/** The id of a session just created; read it with `GET /sessions/{id}`. */
model CreatedSession {
  @format("uuid") id: string;
}

@route("/sessions")
interface Sessions {
  /**
   * Creates a session and returns its id. It starts right away (201) when a slot is free, else
   * it is queued (202); a full queue is a 503.
   */
  @post create(...IdempotencyKey, @body body: CreateSession):
    | (CreatedResponse & { @body body: CreatedSession })
    | (AcceptedResponse & { @body body: CreatedSession })
    | BadRequest
    | UnauthorizedResponse
    | ServiceUnavailable;

  /** Searches sessions, newest first. */
  @route("/search")
  @post search(@body body: SessionSearch): SessionPage | BadRequest | UnauthorizedResponse;

  @get get(...SessionId): Session | BadRequest | NotFound | UnauthorizedResponse;

  /**
   * Stops a session: a queued one is cancelled, a working one is killed (the returned session's
   * status says which). A session that already ended is a 409.
   */
  @route("/{id}/kill")
  @post kill(...SessionId, @body body: KillSession):
    | Session
    | BadRequest
    | NotFound
    | Conflict
    | UnauthorizedResponse;

  /** Deletes a session that has ended; one that hasn't is a 409. */
  @delete delete(...SessionId):
    | NoContentResponse
    | BadRequest
    | NotFound
    | Conflict
    | UnauthorizedResponse;
}

/** An event about one session. */
model SessionEvent<Type extends string> {
  ...Event<Type>;
  @format("uuid") sessionId: string;
}

/** A session moved to another state; `previous` is the state it left. */
model SessionTransition<Type extends string> {
  ...SessionEvent<Type>;
  previous: SessionStatus;
}

/** A session was created (queued or about to start). */
model SessionCreated {
  ...SessionEvent<"session.created">;
  session: Session;
}

/** A session is waiting for a slot. */
model SessionQueued {
  ...SessionEvent<"session.queued">;

  /** 1 = next to start. */
  position: int32;
}

/** A session's agent started. */
model SessionStarted {
  ...SessionTransition<"session.started">;
  providerSessionId: string;
}

/** A session's agent finished. */
model SessionCompleted {
  ...SessionTransition<"session.completed">;
  exitCode: int32;
}

/** A session's agent failed (or couldn't start). */
model SessionFailed {
  ...SessionTransition<"session.failed">;
  error: string;
}

/** A queued session was cancelled before it started: by a user or the system. */
model SessionCancelled {
  ...SessionTransition<"session.cancelled">;
  reason?: string;
  source: KillSource;

  /** The `caller` of the kill request. Set when `source` is `user`. */
  caller?: string;
}

/** A working session was killed: by a user, the system, or its timeout. */
model SessionKilled {
  ...SessionTransition<"session.killed">;
  reason?: string;
  source: KillSource;

  /** The `caller` of the kill request. Set when `source` is `user`. */
  caller?: string;
}
