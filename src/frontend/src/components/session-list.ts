import {
  type ArmEventData,
  eventsStream,
  type Session,
  type SessionStatus,
  sessionsSearch,
} from "@arm/client";
import type { Client } from "@arm/client/client";
import { BaseElement, escapeHtml } from "../base-element.js";

type Status = "idle" | "loading" | "ready" | "error";

/** How many of the newest sessions the list shows. */
const PAGE_SIZE = 50;

/** Prompt characters shown per row before truncating. */
const PROMPT_PREVIEW = 120;

/**
 * `<session-list>` — a read-only view of the newest sessions, kept current from the event
 * stream. Driven entirely by the TypeScript client Hey API generates from the TypeSpec contract
 * (`@arm/client`).
 *
 * Every endpoint needs a bearer token, so the list only talks to the API while `signedIn` is
 * true; signed out it shows a sign-in prompt (its button dispatches a bubbling `sign-in` event
 * for the page to act on) and holds no data or connection.
 *
 * Signed in, it loads one page via `sessionsSearch`, then subscribes to `session.*` on
 * `GET /api/events`: `session.created` inserts the new session at the top, and the lifecycle
 * events patch the matching row in place. The generated SSE client reconnects on its own and
 * sends `Last-Event-ID`, so events missed during a blip are replayed.
 *
 * Every state transition ends by calling `this.render()` **explicitly** — there is no automatic
 * re-render (see {@link BaseElement}).
 */
export class SessionList extends BaseElement {
  static readonly tagName = "session-list";

  #client: Client | null = null;
  #signedIn = false;
  #events: AbortController | null = null;

  // --- view state ---
  #sessions: Session[] = [];
  #total = 0;
  #status: Status = "idle";
  #error = "";
  #live = false;

  /** The API client (see `createApiClient`). */
  set client(value: Client) {
    this.#client = value;
    this.#sync();
  }

  /** Whether the user is signed in. Signing in loads + subscribes; signing out clears. */
  set signedIn(value: boolean) {
    if (value === this.#signedIn) return;
    this.#signedIn = value;
    this.#sync();
  }

  get signedIn(): boolean {
    return this.#signedIn;
  }

  connectedCallback(): void {
    this.render(); // author-triggered first paint
    this.#sync();
  }

  disconnectedCallback(): void {
    this.#unsubscribe();
  }

  /** Start or stop talking to the API to match (connected, client, signed in). */
  #sync(): void {
    if (!this.isConnected) return;
    if (this.#client && this.#signedIn) {
      void this.load();
      this.#subscribe();
      return;
    }
    this.#unsubscribe();
    this.#sessions = [];
    this.#total = 0;
    this.#status = "idle";
    this.#error = "";
    this.render();
  }

  // --- data operations. Each mutates state, then explicitly re-renders. ---

  async load(): Promise<void> {
    if (!this.#client || !this.#signedIn) return;
    this.#status = "loading";
    this.#error = "";
    this.render();
    try {
      const page = await unwrap(
        sessionsSearch({ client: this.#client, body: { limit: PAGE_SIZE } }),
      );
      this.#sessions = page.items;
      this.#total = page.total;
      this.#status = "ready";
    } catch (error) {
      this.#status = "error";
      this.#error = describeError(error);
    }
    this.render();
  }

  /** Open the `session.*` event stream (once); it runs until {@link #unsubscribe}. */
  #subscribe(): void {
    if (this.#events || !this.#client) return;
    const events = new AbortController();
    this.#events = events;
    void (async () => {
      const { stream } = await eventsStream({
        client: this.#client ?? undefined,
        query: { event: ["session.*"] },
        signal: events.signal,
        onSseEvent: () => this.#setLive(events, true),
        onSseError: () => this.#setLive(events, false),
      });
      for await (const data of stream) {
        if (events.signal.aborted) break;
        this.apply(data as ArmEventData);
      }
    })();
  }

  #unsubscribe(): void {
    this.#events?.abort();
    this.#events = null;
    this.#live = false;
  }

  /** Show whether the stream is connected — ignoring a stream that's already been replaced. */
  #setLive(events: AbortController, live: boolean): void {
    if (events !== this.#events || live === this.#live) return;
    this.#live = live;
    this.render();
  }

  /** Fold one event into the list: insert on create, patch the row on lifecycle changes. */
  apply(event: ArmEventData): void {
    if (event.type === "heartbeat") return;
    if (event.type === "session.created") {
      if (this.#sessions.some((s) => s.id === event.sessionId)) return; // replayed
      this.#sessions = [event.session, ...this.#sessions].slice(0, PAGE_SIZE);
      this.#total++;
      this.render();
      return;
    }

    const index = this.#sessions.findIndex((s) => s.id === event.sessionId);
    const current = this.#sessions[index];
    if (!current) return; // not on this page

    let next: Session = { ...current };
    switch (event.type) {
      case "session.queued":
        next = { ...next, status: "queued", queuePosition: event.position };
        break;
      case "session.started":
        next = { ...next, providerSessionId: event.providerSessionId, startedAt: event.at };
        next = moveTo(next, "working");
        break;
      case "session.waiting":
        next = moveTo(next, "waiting");
        break;
      case "session.resumed":
        next = moveTo(next, "working");
        break;
      case "session.completed":
        next = { ...moveTo(next, "completed"), endedAt: event.at, exitCode: event.exitCode };
        if (event.summary !== undefined) next.summary = event.summary;
        break;
      case "session.failed":
        next = { ...moveTo(next, "failed"), endedAt: event.at, error: event.error };
        break;
      case "session.killed":
        next = { ...moveTo(next, "killed"), endedAt: event.at, killSource: event.source };
        if (event.reason !== undefined) next.killReason = event.reason;
        break;
    }
    this.#sessions = this.#sessions.map((s, i) => (i === index ? next : s));
    this.render();
  }

  protected override styles(): string {
    return `
      :host { display: block; }
      .bar { display: flex; align-items: baseline; justify-content: space-between; gap: 1rem; margin-bottom: 0.75rem; }
      .count { opacity: 0.65; font-size: 0.85rem; }
      .live { font-size: 0.78rem; opacity: 0.7; }
      .live::before { content: ""; display: inline-block; width: 0.5rem; height: 0.5rem; margin-right: 0.35rem; border-radius: 50%; background: #9a9a9a; }
      .live.on::before { background: #3aa66a; }
      button { font: inherit; cursor: pointer; border: 1px solid var(--edge, rgba(128,128,128,0.35)); border-radius: 6px; background: transparent; color: inherit; padding: 0.3rem 0.7rem; }
      button:hover { background: rgba(128,128,128,0.12); }
      ul { list-style: none; margin: 0; padding: 0; }
      li { padding: 0.65rem 0; border-top: 1px solid var(--edge, rgba(128,128,128,0.2)); }
      .meta { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0.3rem 0.7rem; font-size: 0.82rem; }
      .meta .dim { opacity: 0.6; }
      .id { font: 0.75rem ui-monospace, monospace; opacity: 0.55; }
      .prompt { margin-top: 0.25rem; overflow-wrap: anywhere; }
      .status { font-size: 0.75rem; font-weight: 600; padding: 0.05rem 0.45rem; border-radius: 999px; background: rgba(128,128,128,0.16); }
      .status.working, .status.waiting { background: rgba(60,130,220,0.18); }
      .status.completed { background: rgba(58,166,106,0.18); }
      .status.failed, .status.killed { background: rgba(200,60,60,0.16); }
      .error { margin: 0.75rem 0; padding: 0.6rem 0.8rem; border-radius: 6px; background: rgba(200,60,60,0.14); font-size: 0.88rem; }
      .muted { opacity: 0.6; padding: 1rem 0; }
      .signin { display: flex; align-items: center; gap: 0.8rem; padding: 1rem 0; }
    `;
  }

  protected override template(): string {
    if (!this.#signedIn) {
      return `
        <div class="signin">
          <span class="muted">Sign in to see sessions.</span>
          <button data-action="sign-in">Log in</button>
        </div>`;
    }
    return `
      <div class="bar">
        <span class="count">${this.#countLabel()}</span>
        <span>
          <span class="live ${this.#live ? "on" : ""}">${this.#live ? "Live" : "Connecting…"}</span>
          <button data-action="refresh">Refresh</button>
        </span>
      </div>
      ${this.#error ? `<div class="error">${escapeHtml(this.#error)}</div>` : ""}
      ${this.#body()}
    `;
  }

  #countLabel(): string {
    if (this.#status === "loading") return "Loading…";
    if (this.#status === "error") return "Error";
    const n = this.#total;
    const shown = this.#sessions.length < n ? `newest ${this.#sessions.length} of ` : "";
    return `${shown}${n} session${n === 1 ? "" : "s"}`;
  }

  #body(): string {
    if (this.#status === "loading" && this.#sessions.length === 0) {
      return `<p class="muted">Loading sessions…</p>`;
    }
    if (this.#status === "ready" && this.#sessions.length === 0) {
      return `<p class="muted">No sessions yet.</p>`;
    }
    return `<ul>${this.#sessions.map((s) => row(s)).join("")}</ul>`;
  }

  protected override afterRender(): void {
    this.query<HTMLButtonElement>('[data-action="refresh"]')?.addEventListener(
      "click",
      () => void this.load(),
    );
    this.query<HTMLButtonElement>('[data-action="sign-in"]')?.addEventListener("click", () =>
      this.dispatchEvent(new CustomEvent("sign-in", { bubbles: true, composed: true })),
    );
  }
}

function row(session: Session): string {
  const queued =
    session.status === "queued" && session.queuePosition !== undefined
      ? `<span class="queue">#${session.queuePosition} in queue</span>`
      : "";
  const caller = session.caller ? `<span class="caller">${escapeHtml(session.caller)}</span>` : "";
  return `
    <li data-id="${escapeHtml(session.id)}">
      <div class="meta">
        <span class="status ${escapeHtml(session.status)}">${escapeHtml(session.status)}</span>
        ${queued}
        <span class="id" title="${escapeHtml(session.id)}">${escapeHtml(session.id.slice(0, 8))}</span>
        <span class="provider dim">${escapeHtml(session.provider)}</span>
        ${caller}
        <time class="dim" datetime="${escapeHtml(session.createdAt)}">${escapeHtml(formatTime(session.createdAt))}</time>
      </div>
      <div class="prompt">${escapeHtml(truncate(session.prompt, PROMPT_PREVIEW))}</div>
    </li>`;
}

/** A session in a new status; the queue position only means something while queued. */
function moveTo(session: Session, status: SessionStatus): Session {
  const { queuePosition: _, ...rest } = session;
  return { ...rest, status };
}

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

function formatTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" });
}

/**
 * A failed API call: the HTTP status (undefined when no response arrived, e.g. a network error)
 * and the parsed error body — the API's `{ error: { code, message, details } }` envelope.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number | undefined,
    readonly body: unknown,
  ) {
    super(envelopeMessage(body) ?? (status ? `Request failed (${status}).` : "Request failed."));
    this.name = "ApiError";
  }
}

/**
 * The generated SDK returns `{ data, error, response }` instead of throwing (and a thrown error
 * would carry only the body, not the status). Turn a failure into an {@link ApiError} with the
 * status so the component's try/catch flow stays simple.
 */
async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response?: Response }>,
): Promise<T> {
  const { data, error, response } = await call;
  if (error !== undefined || !response?.ok) throw new ApiError(response?.status, error);
  return data as T;
}

/** The `error.message` of an error envelope, or a thrown Error's own message. */
function envelopeMessage(body: unknown): string | undefined {
  const message = (body as { error?: { message?: unknown } } | undefined)?.error?.message;
  if (typeof message === "string" && message) return message;
  if (body instanceof Error && body.message) return body.message;
  return undefined;
}

/** Turn a failed call into a human-readable line, calling out the auth case. */
function describeError(error: unknown): string {
  const status = error instanceof ApiError ? error.status : undefined;
  if (status === 401 || status === 403) return "Not authorized — try logging in again.";
  return (error as { message?: string } | undefined)?.message || "Request failed.";
}
