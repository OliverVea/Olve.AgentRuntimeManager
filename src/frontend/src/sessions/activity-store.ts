import { type ActivityLine, type Session, sessionActivityGet } from "@arm/client";
import type { Client } from "@arm/client/client";
import { unwrap } from "../api-errors.js";

/** How often working sessions' activity is re-read (live updates come with M6). */
export const ACTIVITY_MS = 3000;

/** Lines read per session: a card shows the last 3 (8 wrapped, older ones leaving at the top), expanded all of them. */
export const ACTIVITY_LINES = 20;

/** Where the working sessions come from: a {@link SessionStore}, which says `change`. */
export type ActiveSessions = EventTarget & { readonly active: Session[] };

/**
 * The Overview's log column (M5f): the latest activity of every working session, read with
 * `GET /api/sessions/{id}/activity`. While started, it reads every working session's at once, and
 * again {@link ACTIVITY_MS} later while any still works; a session that starts working is read
 * right away. It stops when the page is hidden (and reads again when it's shown), when nothing
 * works, and on `stop()`. A session that's no longer working loses its lines. A failed read keeps
 * the lines it had until the next one.
 *
 * Dispatches `change` when some session's lines changed.
 */
export class ActivityStore extends EventTarget {
  readonly #client: Client;
  readonly #sessions: ActiveSessions;
  readonly #intervalMs: number;
  readonly #document: Document;

  #lines = new Map<string, ActivityLine[]>();
  #started = false;
  #timer: ReturnType<typeof setTimeout> | undefined;
  #reading = false;
  /** Asked to read while a read was in flight: read again when it's in. */
  #again = false;

  readonly #onSessions = () => this.#sessionsChanged();
  readonly #onVisibility = () => {
    if (this.#document.hidden) clearTimeout(this.#timer);
    else void this.#read();
  };

  constructor(
    client: Client,
    sessions: ActiveSessions,
    options: { intervalMs?: number; document?: Document } = {},
  ) {
    super();
    this.#client = client;
    this.#sessions = sessions;
    this.#intervalMs = options.intervalMs ?? ACTIVITY_MS;
    this.#document = options.document ?? document;
  }

  /** A working session's latest lines, oldest first; undefined until they're read. */
  lines(id: string): readonly ActivityLine[] | undefined {
    return this.#lines.get(id);
  }

  start(): void {
    if (this.#started) return;
    this.#started = true;
    this.#sessions.addEventListener("change", this.#onSessions);
    this.#document.addEventListener("visibilitychange", this.#onVisibility);
    void this.#read();
  }

  /** Stops reading; the lines read so far stay for when it starts again. */
  stop(): void {
    this.#started = false;
    clearTimeout(this.#timer);
    this.#sessions.removeEventListener("change", this.#onSessions);
    this.#document.removeEventListener("visibilitychange", this.#onVisibility);
  }

  #working(): string[] {
    return this.#sessions.active.filter((s) => s.status === "working").map((s) => s.id);
  }

  #sessionsChanged(): void {
    const working = new Set(this.#working());
    let dropped = false;
    for (const id of this.#lines.keys()) {
      if (!working.has(id)) dropped = this.#lines.delete(id) || dropped;
    }
    if (dropped) this.dispatchEvent(new Event("change"));
    if (!working.size) clearTimeout(this.#timer);
    // A session that just started working is read now rather than at the next tick.
    else if ([...working].some((id) => !this.#lines.has(id))) void this.#read();
  }

  async #read(): Promise<void> {
    if (!this.#started || this.#document.hidden) return;
    if (this.#reading) {
      this.#again = true;
      return;
    }
    clearTimeout(this.#timer);
    const ids = this.#working();
    if (!ids.length) return;

    this.#reading = true;
    const read = await Promise.all(
      ids.map((id) =>
        unwrap(
          sessionActivityGet({
            client: this.#client,
            path: { id },
            query: { limit: ACTIVITY_LINES },
          }),
        ).then(
          (activity) => [id, activity.lines] as const,
          () => undefined, // keep what it had; the next read tries again
        ),
      ),
    );
    this.#reading = false;

    const working = new Set(this.#working());
    let changed = false;
    for (const [id, lines] of read.filter((r) => r !== undefined)) {
      if (!working.has(id) || sameLines(this.#lines.get(id), lines)) continue;
      this.#lines.set(id, lines);
      changed = true;
    }
    if (changed) this.dispatchEvent(new Event("change"));

    if (this.#again) {
      this.#again = false;
      void this.#read();
    } else if (this.#started && !this.#document.hidden && working.size) {
      this.#timer = setTimeout(() => void this.#read(), this.#intervalMs);
    }
  }
}

function sameLines(a: readonly ActivityLine[] | undefined, b: readonly ActivityLine[]): boolean {
  return (
    a !== undefined &&
    a.length === b.length &&
    a.every((line, i) => line.kind === b[i]?.kind && line.text === b[i]?.text)
  );
}
