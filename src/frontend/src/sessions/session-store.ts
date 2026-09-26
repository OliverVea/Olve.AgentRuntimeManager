import {
  type ArmEventData,
  eventsStream,
  type Session,
  type SessionStatus,
  sessionsSearch,
} from "@arm/client";
import type { Client } from "@arm/client/client";
import { describeError, unwrap } from "../api-errors.js";
import { activeStatuses, endedStatuses, isEnded } from "./format.js";

/** The live connection: the header's dot. `down` also covers "never connected". */
export type StreamState = "connecting" | "connected" | "reconnecting" | "down";

export type LoadState = "idle" | "loading" | "ready" | "error";

type SessionEvent = Exclude<ArmEventData, { type: "heartbeat" }>;

/** How many active sessions the Overview loads (the search maximum). */
const ACTIVE_LIMIT = 100;

/** Failed (re)connects in a row after which the stream counts as down rather than reconnecting. */
const DOWN_AFTER_ERRORS = 3;

/** Queued, working, ended: a session only moves forward. */
function rank(status: SessionStatus): number {
  if (status === "queued") return 0;
  if (status === "working") return 1;
  return 2;
}

/**
 * The sessions the sessions screen shows, kept current from `GET /api/events`.
 *
 * Every (re)connect fetches the Overview's snapshot (queued + working) right away, so the list
 * loads even while the stream is down, and again on the connection's first heartbeat: the server
 * sends it once subscribed, so that snapshot can't miss a change. Events that arrive while a
 * snapshot loads are buffered and applied on top of it. Until
 * events carry a per-session `seq` (M5b/M6), duplicates and stale events are recognised by the
 * lifecycle: a `created` for a known session is ignored, and no event moves a session backwards
 * (queued → working → ended).
 *
 * History (ended sessions, newest first) loads a page at a time on demand; sessions that end
 * while the page is open join it from their events.
 *
 * Dispatches `change` after every update.
 */
export class SessionStore extends EventTarget {
  readonly #client: Client;
  readonly #reconnectDelayMs: number;

  #sessions = new Map<string, Session>();
  #run: AbortController | null = null;
  /** Events held back while a snapshot is loading. */
  #buffer: SessionEvent[] | null = null;
  #everConnected = false;
  /** Connected, but the server hasn't confirmed its subscription (the connect heartbeat) yet. */
  #awaitingHeartbeat = false;
  /** A snapshot was asked for while one was loading: take another when it's in. */
  #snapshotAgain = false;
  #errors = 0;

  stream: StreamState = "connecting";
  overview: LoadState = "idle";
  history: LoadState = "idle";
  error = "";
  /** How many ended sessions the server has, and how many of them History has loaded. */
  historyTotal = 0;
  historyLoaded = 0;

  constructor(client: Client, options: { reconnectDelayMs?: number } = {}) {
    super();
    this.#client = client;
    this.#reconnectDelayMs = options.reconnectDelayMs ?? 1000;
  }

  /** Queued and working sessions, oldest first, with their queue positions. */
  get active(): Session[] {
    const active = [...this.#sessions.values()]
      .filter((s) => !isEnded(s))
      .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
    let position = 0;
    return active.map((s) => (s.status === "queued" ? { ...s, queuePosition: ++position } : s));
  }

  /** Ended sessions, newest first. */
  get ended(): Session[] {
    return [...this.#sessions.values()]
      .filter(isEnded)
      .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  }

  get(id: string): Session | undefined {
    return this.#sessions.get(id);
  }

  /** Start following the API (idempotent). */
  start(): void {
    if (this.#run) return;
    this.#run = new AbortController();
    this.#everConnected = false;
    this.#errors = 0;
    this.stream = "connecting";
    void this.#follow(this.#run);
  }

  /** Stop following and forget everything (e.g. on logout). */
  stop(): void {
    this.#run?.abort();
    this.#run = null;
    this.#buffer = null;
    this.#awaitingHeartbeat = false;
    this.#snapshotAgain = false;
    this.#sessions = new Map();
    this.stream = "connecting";
    this.overview = "idle";
    this.history = "idle";
    this.error = "";
    this.historyTotal = 0;
    this.historyLoaded = 0;
    this.#changed();
  }

  /** A session this page got from the API (e.g. the create response): newer data wins. */
  upsert(session: Session): void {
    this.#merge(session);
    this.#changed();
  }

  /** Forget a deleted session. */
  remove(id: string): void {
    if (!this.#sessions.delete(id)) return;
    if (this.historyLoaded > 0) {
      this.historyLoaded--;
      this.historyTotal = Math.max(0, this.historyTotal - 1);
    }
    this.#changed();
  }

  /** Load History's first page, or (`more`) the next older one. */
  async loadHistory(more = false): Promise<void> {
    if (this.history === "loading") return;
    this.history = "loading";
    this.#changed();
    try {
      const offset = more ? this.historyLoaded : 0;
      const page = await unwrap(
        sessionsSearch({
          client: this.#client,
          body: { status: [...endedStatuses], limit: 20, offset },
        }),
      );
      for (const session of page.items) this.#merge(session);
      this.historyLoaded = offset + page.items.length;
      this.historyTotal = page.total;
      this.history = "ready";
    } catch (error) {
      this.history = "error";
      this.error = describeError(error);
    }
    this.#changed();
  }

  /** Apply one event (exposed for tests). */
  apply(event: ArmEventData): void {
    if (event.type === "heartbeat") return;
    if (this.#buffer) {
      this.#buffer.push(event);
      return;
    }
    this.#apply(event);
    this.#changed();
  }

  /** Opens the stream until `run` is aborted, reopening it whenever the server ends it. */
  async #follow(run: AbortController): Promise<void> {
    while (!run.signal.aborted) {
      void this.#snapshot(run);
      try {
        const { stream } = await eventsStream({
          client: this.#client,
          query: { event: ["session.*"] },
          signal: run.signal,
          fetch: this.#watchedFetch(run),
          onSseError: () => this.#failed(run),
        });
        for await (const data of stream) {
          if (run.signal.aborted) return;
          const event = data as ArmEventData;
          // The server subscribes before its connect heartbeat: a snapshot taken after it can't
          // miss anything, since every later change arrives as an event.
          if (event.type === "heartbeat" && this.#awaitingHeartbeat) {
            this.#awaitingHeartbeat = false;
            void this.#snapshot(run);
          }
          this.apply(event);
        }
      } catch {
        // Aborted, or the stream broke outside the client's own retries: reopen below.
      }
      if (run.signal.aborted) return;
      // The server ended the stream (e.g. a redeploy): reconnect with a fresh snapshot.
      this.#setStream(run, "reconnecting");
      await new Promise((resolve) => setTimeout(resolve, this.#reconnectDelayMs));
    }
  }

  /** The client's fetch, noting each successful (re)connect of the stream. */
  #watchedFetch(run: AbortController): typeof fetch {
    const base = this.#client.getConfig().fetch ?? globalThis.fetch;
    return (async (input: RequestInfo | URL, init?: RequestInit) => {
      const response = await base(input, init);
      if (response.ok && run === this.#run) {
        this.#everConnected = true;
        this.#awaitingHeartbeat = true;
        this.#errors = 0;
        this.#setStream(run, "connected");
      }
      return response;
    }) as typeof fetch;
  }

  #failed(run: AbortController): void {
    if (run !== this.#run || run.signal.aborted) return;
    this.#errors++;
    const down = !this.#everConnected || this.#errors >= DOWN_AFTER_ERRORS;
    this.#setStream(run, down ? "down" : "reconnecting");
  }

  #setStream(run: AbortController, state: StreamState): void {
    if (run !== this.#run || this.stream === state) return;
    this.stream = state;
    this.#changed();
  }

  /** Fetch the queued and working sessions, holding events back until they're in. */
  async #snapshot(run: AbortController): Promise<void> {
    if (this.#buffer) {
      this.#snapshotAgain = true; // it may have been read before the server subscribed
      return;
    }
    this.#buffer = [];
    if (this.overview !== "ready") this.overview = "loading";
    this.#changed();
    try {
      const page = await unwrap(
        sessionsSearch({
          client: this.#client,
          body: { status: [...activeStatuses], limit: ACTIVE_LIMIT },
          signal: run.signal,
        }),
      );
      if (run !== this.#run) return;
      // The snapshot is the truth about what's active: a session it lacks ended (or was
      // deleted) while we weren't looking.
      const fresh = new Set(page.items.map((s) => s.id));
      for (const s of [...this.#sessions.values()]) {
        if (!isEnded(s) && !fresh.has(s.id)) this.#sessions.delete(s.id);
      }
      for (const session of page.items) this.#merge(session);
      this.overview = "ready";
      this.error = "";
    } catch (error) {
      if (run !== this.#run) return;
      this.overview = "error";
      this.error = describeError(error);
    }
    const buffered = this.#buffer ?? [];
    this.#buffer = null;
    for (const event of buffered) this.#apply(event);
    this.#changed();
    if (this.#snapshotAgain && run === this.#run) {
      this.#snapshotAgain = false;
      void this.#snapshot(run);
    }
  }

  /** Keep whichever of the known and the given session is further along. */
  #merge(session: Session): void {
    const known = this.#sessions.get(session.id);
    if (known && rank(known.status) > rank(session.status)) return;
    this.#sessions.set(session.id, session);
  }

  #apply(event: SessionEvent): void {
    if (event.type === "session.created") {
      if (!this.#sessions.has(event.sessionId)) this.#sessions.set(event.sessionId, event.session);
      return;
    }
    const current = this.#sessions.get(event.sessionId);
    if (!current) return; // not one this page shows
    const next = patch(current, event);
    if (
      rank(next.status) < rank(current.status) ||
      (isEnded(current) && next.status !== current.status)
    ) {
      return; // stale: the session is already further along
    }
    this.#sessions.set(current.id, next);
  }

  #changed(): void {
    this.dispatchEvent(new Event("change"));
  }
}

/** A session after one of its lifecycle events. */
function patch(
  session: Session,
  event: Exclude<SessionEvent, { type: "session.created" }>,
): Session {
  switch (event.type) {
    case "session.queued":
      return { ...session, status: "queued", queuePosition: event.position };
    case "session.started":
      return {
        ...moveTo(session, "working"),
        providerSessionId: event.providerSessionId,
        startedAt: event.at,
      };
    case "session.completed": {
      const next: Session = {
        ...moveTo(session, "completed"),
        endedAt: event.at,
        exitCode: event.exitCode,
      };
      if (event.summary !== undefined) next.summary = event.summary;
      return next;
    }
    case "session.failed":
      return { ...moveTo(session, "failed"), endedAt: event.at, error: event.error };
    case "session.cancelled":
    case "session.killed": {
      const next: Session = {
        ...moveTo(session, event.type === "session.cancelled" ? "cancelled" : "killed"),
        endedAt: event.at,
        killSource: event.source,
      };
      if (event.reason !== undefined) next.killReason = event.reason;
      if (event.caller !== undefined) next.killCaller = event.caller;
      return next;
    }
  }
}

/** A session in a new status; the queue position only means something while queued. */
function moveTo(session: Session, status: SessionStatus): Session {
  const { queuePosition: _, ...rest } = session;
  return { ...rest, status };
}
