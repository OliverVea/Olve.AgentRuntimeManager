import { BaseElement, escapeHtml } from "../base-element.js";
import { isEnded } from "../sessions/format.js";
import type { SessionStore } from "../sessions/session-store.js";
import { cardStyles, sessionCard, type TimesAs, timeOf } from "./session-card.js";

export type View = "overview" | "history";

/** `<use href>` only finds symbols in its own tree, so the cards' icons live in the shadow root. */
const sprite = `<svg width="0" height="0" style="position:absolute" aria-hidden="true">
  <symbol id="i-x" viewBox="0 0 24 24"><path d="M18 6 6 18M6 6l12 12"/></symbol>
  <symbol id="i-trash" viewBox="0 0 24 24"><path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></symbol>
</svg>`;

/**
 * `<session-list>` — the Overview (queued and working, oldest first) or History (ended, newest
 * first) as cards, from a {@link SessionStore}. It re-renders on every store change, and only
 * ticks the running times in between; the composer and dialogs live outside it, so a live update
 * never touches what the user is typing.
 *
 * It only reports what the user wants, as bubbling events whose `detail` is the session id:
 * `open-session`, `kill-session`, `delete-session`, `copy-id`; plus `toggle-times`.
 */
export class SessionList extends BaseElement {
  static readonly tagName = "session-list";

  #store: SessionStore | null = null;
  #view: View = "overview";
  #times: TimesAs = "relative";
  #tick: ReturnType<typeof setInterval> | undefined;
  readonly #onChange = () => this.render();

  set store(store: SessionStore) {
    this.#store?.removeEventListener("change", this.#onChange);
    this.#store = store;
    store.addEventListener("change", this.#onChange);
    this.render();
  }

  set view(view: View) {
    this.#view = view;
    if (view === "history" && this.#store?.history === "idle") void this.#store.loadHistory();
    this.render();
  }

  get view(): View {
    return this.#view;
  }

  set times(times: TimesAs) {
    this.#times = times;
    this.render();
  }

  constructor() {
    super();
    this.root.addEventListener("click", (e) => this.#click(e as MouseEvent));
    this.root.addEventListener("keydown", (e) => this.#key(e as KeyboardEvent));
  }

  connectedCallback(): void {
    this.render();
    this.#tick = setInterval(() => this.#tickTimes(), 1000);
  }

  disconnectedCallback(): void {
    clearInterval(this.#tick);
  }

  protected override styles(): string {
    return cardStyles;
  }

  protected override template(): string {
    const store = this.#store;
    if (!store) return "";
    const now = Date.now();
    const overview = this.#view === "overview";
    const sessions = overview ? store.active : store.ended;
    const state = overview ? store.overview : store.history;
    const error = state === "error" ? `<div class="error">${escapeHtml(store.error)}</div>` : "";

    let body: string;
    if (sessions.length) body = sessions.map((s) => sessionCard(s, this.#times, now)).join("");
    else if (state === "loading" || state === "idle") body = `<div class="empty">Loading…</div>`;
    else if (overview) body = `<div class="empty">Nothing running. Start a session above.</div>`;
    else body = `<div class="empty">No ended sessions yet.</div>`;

    const older = !overview && store.historyLoaded < store.historyTotal;
    const footer = older
      ? `<div class="footer-note">Showing the newest ${sessions.length} · <button data-older>load older</button></div>`
      : "";
    return sprite + error + body + footer;
  }

  /** Keep keyboard focus on the same card across re-renders. */
  protected override render(): void {
    const focused = (this.root.activeElement as HTMLElement | null)?.dataset?.open;
    super.render();
    this.#markOverflow();
    if (focused) this.query<HTMLElement>(`[data-open="${CSS.escape(focused)}"]`)?.focus();
  }

  /** Fade rows that are wider than the card. */
  #markOverflow(): void {
    for (const row of this.queryAll<HTMLElement>(".info, .r1")) {
      row.classList.toggle("overflows", row.scrollWidth > row.clientWidth + 1);
    }
  }

  #tickTimes(): void {
    const now = Date.now();
    for (const el of this.queryAll<HTMLElement>("[data-dur]")) {
      const session = this.#store?.get(el.dataset.dur ?? "");
      if (session && !isEnded(session)) el.textContent = timeOf(session, this.#times, now);
    }
  }

  #emit(type: string, detail?: string): void {
    this.dispatchEvent(new CustomEvent(type, { detail, bubbles: true, composed: true }));
  }

  #click(e: MouseEvent): void {
    const target = e.target as HTMLElement;
    const control = target.closest<HTMLElement>("button, [data-copy]");
    if (!control) {
      // A click anywhere else on a card opens that session (unless it ends a text selection).
      const card = target.closest<HTMLElement>("[data-open]");
      if (card && !getSelection()?.toString()) this.#emit("open-session", card.dataset.open);
      return;
    }
    const d = control.dataset;
    if (d.kill) this.#emit("kill-session", d.kill);
    else if (d.delete) this.#emit("delete-session", d.delete);
    else if (d.copy) this.#emit("copy-id", d.copy);
    else if ("times" in d) this.#emit("toggle-times");
    else if ("older" in d) void this.#store?.loadHistory(true);
  }

  /** Enter opens the focused card; Delete asks to kill (or delete) it. */
  #key(e: KeyboardEvent): void {
    const card = (e.target as HTMLElement).closest?.<HTMLElement>("[data-open]");
    const id = card?.dataset.open;
    if (!id) return;
    if (e.key === "Enter" && e.target === card) {
      this.#emit("open-session", id);
    } else if (e.key === "Delete") {
      const session = this.#store?.get(id);
      if (!session) return;
      e.preventDefault();
      this.#emit(isEnded(session) ? "delete-session" : "kill-session", id);
    }
  }
}
