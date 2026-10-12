import { type ActivityLine, type Session, sessionActivityGet } from "@arm/client";
import type { Client } from "@arm/client/client";
import { unwrap } from "../api-errors.js";

/** How often a working session's activity is re-read (live updates come with M6). */
export const ACTIVITY_MS = 3000;

/** How long one read may take before it's given up (and tried again at the usual pace). */
export const ACTIVITY_TIMEOUT_MS = 10_000;

/** Lines read for a session unless a card asks for more ({@link ActivityStore.want}): a collapsed card shows 3. */
export const DEFAULT_LINES = 3;

/** Where the working sessions come from: a {@link SessionStore}, which says `change`. */
export type ActiveSessions = EventTarget & { readonly active: Session[] };

/** The `detail` of a `change`: whose lines changed. */
export type ActivityChange = { id: string };

/** One working session's reading: the read in flight, or the timer for the next one. */
type Reader = { read?: AbortController; timer?: ReturnType<typeof setTimeout> };

/**
 * The Overview's log column (M5f): the latest activity of every working session, read with
 * `GET /api/sessions/{id}/activity`, as many lines as its card shows ({@link DEFAULT_LINES} unless
 * {@link want} says otherwise). Each working session is read on its own: once when it starts
 * working (or when its card wants more lines), then {@link ACTIVITY_MS} after each read ends,
 * whether it worked or not, so a slow or failing session neither holds back the others nor is
 * read more often. A read is given up after {@link ACTIVITY_TIMEOUT_MS}. Reading stops while the
 * page is hidden (and starts again when it's shown), and on `stop()`, which aborts the reads in
 * flight. A session that's no longer working loses its lines. A failed read keeps the lines it had.
 *
 * Dispatches `change` (a `CustomEvent` with {@link ActivityChange}) when a session's lines changed.
 */
export class ActivityStore extends EventTarget {
  readonly #client: Client;
  readonly #sessions: ActiveSessions;
  readonly #intervalMs: number;
  readonly #timeoutMs: number;
  readonly #document: Document;

  #lines = new Map<string, ActivityLine[]>();
  #limits = new Map<string, number>();
  #readers = new Map<string, Reader>();
  #started = false;

  readonly #onSessions = () => this.#sync();
  readonly #onVisibility = () => {
    if (this.#document.hidden) this.#halt();
    else this.#sync();
  };

  constructor(
    client: Client,
    sessions: ActiveSessions,
    options: { intervalMs?: number; timeoutMs?: number; document?: Document } = {},
  ) {
    super();
    this.#client = client;
    this.#sessions = sessions;
    this.#intervalMs = options.intervalMs ?? ACTIVITY_MS;
    this.#timeoutMs = options.timeoutMs ?? ACTIVITY_TIMEOUT_MS;
    this.#document = options.document ?? document;
  }

  /** A working session's latest lines, oldest first; undefined until they're read. */
  lines(id: string): readonly ActivityLine[] | undefined {
    return this.#lines.get(id);
  }

  /** How many lines a session's card shows; a different number is read at once. */
  want(id: string, limit: number): void {
    if ((this.#limits.get(id) ?? DEFAULT_LINES) === limit) return;
    this.#limits.set(id, limit);
    if (this.#readers.has(id)) void this.#read(id);
  }

  start(): void {
    if (this.#started) return;
    this.#started = true;
    this.#sessions.addEventListener("change", this.#onSessions);
    this.#document.addEventListener("visibilitychange", this.#onVisibility);
    this.#sync();
  }

  /** Stops reading and aborts the reads in flight; the lines read so far stay for when it starts again. */
  stop(): void {
    this.#started = false;
    this.#sessions.removeEventListener("change", this.#onSessions);
    this.#document.removeEventListener("visibilitychange", this.#onVisibility);
    this.#halt();
  }

  #halt(): void {
    for (const reader of this.#readers.values()) {
      clearTimeout(reader.timer);
      reader.read?.abort();
    }
    this.#readers.clear();
  }

  /** Drops what's no longer working, and reads what works and isn't being read yet. */
  #sync(): void {
    const working = new Set(
      this.#sessions.active.filter((s) => s.status === "working").map((s) => s.id),
    );
    for (const [id, reader] of this.#readers) {
      if (working.has(id)) continue;
      clearTimeout(reader.timer);
      reader.read?.abort();
      this.#readers.delete(id);
    }
    for (const id of [...this.#lines.keys(), ...this.#limits.keys()]) {
      if (working.has(id)) continue;
      this.#limits.delete(id);
      if (this.#lines.delete(id)) this.#changed(id);
    }
    if (!this.#started || this.#document.hidden) return;
    for (const id of working) if (!this.#readers.has(id)) void this.#read(id);
  }

  /** Reads one session now (instead of its read in flight or its next one), then again in a while. */
  async #read(id: string): Promise<void> {
    const reader = this.#readers.get(id) ?? {};
    clearTimeout(reader.timer);
    reader.read?.abort();
    const read = new AbortController();
    reader.read = read;
    this.#readers.set(id, reader);
    const timeout = setTimeout(() => read.abort(), this.#timeoutMs);
    try {
      const activity = await unwrap(
        sessionActivityGet({
          client: this.#client,
          path: { id },
          query: { limit: this.#limits.get(id) ?? DEFAULT_LINES },
          signal: read.signal,
        }),
      );
      if (reader.read === read && this.#readers.get(id) === reader) this.#set(id, activity.lines);
    } catch {
      // Keep what it had; the next read tries again.
    } finally {
      clearTimeout(timeout);
    }
    // Superseded (a new read, a stop, the session ended): whoever did that goes on from here.
    if (reader.read !== read || this.#readers.get(id) !== reader) return;
    reader.read = undefined;
    reader.timer = setTimeout(() => void this.#read(id), this.#intervalMs);
  }

  #set(id: string, lines: ActivityLine[]): void {
    if (sameLines(this.#lines.get(id), lines)) return;
    this.#lines.set(id, lines);
    this.#changed(id);
  }

  #changed(id: string): void {
    this.dispatchEvent(new CustomEvent<ActivityChange>("change", { detail: { id } }));
  }
}

function sameLines(a: readonly ActivityLine[] | undefined, b: readonly ActivityLine[]): boolean {
  return (
    a !== undefined &&
    a.length === b.length &&
    a.every((line, i) => line.kind === b[i]?.kind && line.text === b[i]?.text)
  );
}
