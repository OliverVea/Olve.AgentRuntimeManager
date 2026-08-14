import type { Message } from "../api/models/index.js";
import type { OlveTemplateApiClient } from "../api/olveTemplateApiClient.js";
import { BaseElement, escapeHtml } from "../base-element.js";

type Status = "idle" | "loading" | "ready" | "error";

/**
 * `<message-list>` — the template's first real component (DESIGN §2.4): a CRUD view over
 * the backend `Message` feature, driven entirely by the Kiota-generated TypeScript client.
 * Proves the client-gen → component → live-API loop end to end.
 *
 * Every state transition (load, create, edit, delete, page change) ends by calling
 * `this.render()` **explicitly** — there is no automatic re-render (see {@link BaseElement}).
 */
export class MessageList extends BaseElement {
  static readonly tagName = "message-list";

  #client: OlveTemplateApiClient | null = null;

  // --- view state ---
  #messages: Message[] = [];
  #status: Status = "idle";
  #error = "";
  #page = 1;
  #pageSize = 20;
  #totalCount = 0;
  #hasNextPage = false;
  #editingId: string | null = null;

  /** The Kiota client. Setting it (re)loads the first page if the element is connected. */
  set client(value: OlveTemplateApiClient) {
    this.#client = value;
    if (this.isConnected) void this.load();
  }

  connectedCallback(): void {
    this.render(); // author-triggered first paint
    if (this.#client) void this.load();
  }

  // --- data operations. Each mutates state, then explicitly re-renders. ---

  async load(): Promise<void> {
    if (!this.#client) return;
    this.#status = "loading";
    this.#error = "";
    this.render();
    try {
      const page = await this.#client.api.messages.get({
        queryParameters: { page: String(this.#page), pageSize: String(this.#pageSize) },
      });
      this.#messages = page?.items ?? [];
      this.#totalCount = untypedToNumber(page?.totalCount);
      this.#hasNextPage = page?.hasNextPage ?? false;
      this.#status = "ready";
    } catch (error) {
      this.#status = "error";
      this.#error = describeError(error);
    }
    this.render();
  }

  async create(text: string): Promise<void> {
    if (!this.#client || !text.trim()) return;
    try {
      await this.#client.api.messages.post({ text });
      this.#page = 1;
      await this.load();
    } catch (error) {
      this.#error = describeError(error);
      this.render();
    }
  }

  async saveEdit(id: string, text: string): Promise<void> {
    if (!this.#client || !text.trim()) return;
    try {
      await this.#client.api.messages.byId(id).put({ text });
      this.#editingId = null;
      await this.load();
    } catch (error) {
      this.#error = describeError(error);
      this.render();
    }
  }

  async deleteMessage(id: string): Promise<void> {
    if (!this.#client) return;
    try {
      await this.#client.api.messages.byId(id).delete();
      await this.load();
    } catch (error) {
      this.#error = describeError(error);
      this.render();
    }
  }

  #goToPage(page: number): void {
    this.#page = Math.max(1, page);
    this.#editingId = null;
    void this.load();
  }

  protected override styles(): string {
    return `
      :host { display: block; }
      .bar { display: flex; align-items: baseline; justify-content: space-between; gap: 1rem; margin-bottom: 0.75rem; }
      .count { opacity: 0.65; font-size: 0.85rem; }
      button { font: inherit; cursor: pointer; border: 1px solid var(--edge, rgba(128,128,128,0.35)); border-radius: 6px; background: transparent; color: inherit; padding: 0.3rem 0.7rem; }
      button:hover { background: rgba(128,128,128,0.12); }
      ul { list-style: none; margin: 0; padding: 0; }
      li { display: flex; align-items: center; gap: 0.6rem; padding: 0.6rem 0; border-top: 1px solid var(--edge, rgba(128,128,128,0.2)); }
      li .text { flex: 1; }
      li .id { font: 0.72rem ui-monospace, monospace; opacity: 0.45; }
      form, .edit { display: flex; gap: 0.5rem; }
      input[type="text"] { flex: 1; font: inherit; padding: 0.4rem 0.6rem; border: 1px solid var(--edge, rgba(128,128,128,0.35)); border-radius: 6px; background: transparent; color: inherit; }
      .new { margin-top: 1rem; }
      .error { margin: 0.75rem 0; padding: 0.6rem 0.8rem; border-radius: 6px; background: rgba(200,60,60,0.14); font-size: 0.88rem; }
      .muted { opacity: 0.6; padding: 1rem 0; }
      .pager { display: flex; gap: 0.5rem; align-items: center; margin-top: 1rem; }
    `;
  }

  protected override template(): string {
    return `
      <div class="bar">
        <span class="count">${this.#countLabel()}</span>
        <button data-action="refresh">Refresh</button>
      </div>
      ${this.#error ? `<div class="error">${escapeHtml(this.#error)}</div>` : ""}
      ${this.#body()}
      <form class="new">
        <input type="text" name="text" placeholder="New message…" autocomplete="off" />
        <button type="submit">Add</button>
      </form>
      ${this.#pager()}
    `;
  }

  #countLabel(): string {
    if (this.#status === "loading") return "Loading…";
    if (this.#status === "error") return "Error";
    const n = this.#totalCount;
    return `${n} message${n === 1 ? "" : "s"}`;
  }

  #body(): string {
    if (this.#status === "loading" && this.#messages.length === 0) {
      return `<p class="muted">Loading messages…</p>`;
    }
    if (this.#status === "ready" && this.#messages.length === 0) {
      return `<p class="muted">No messages yet — add the first one below.</p>`;
    }
    return `<ul>${this.#messages.map((m) => this.#row(m)).join("")}</ul>`;
  }

  #row(message: Message): string {
    const id = message.id ? String(message.id) : "";
    const text = message.text ?? "";
    if (this.#editingId === id) {
      return `
        <li>
          <div class="edit" style="flex:1">
            <input type="text" data-edit-input="${escapeHtml(id)}" value="${escapeHtml(text)}" />
            <button data-action="save" data-id="${escapeHtml(id)}">Save</button>
            <button data-action="cancel">Cancel</button>
          </div>
        </li>`;
    }
    return `
      <li>
        <span class="text">${escapeHtml(text)}</span>
        <span class="id">${escapeHtml(id.slice(0, 8))}</span>
        <button data-action="edit" data-id="${escapeHtml(id)}">Edit</button>
        <button data-action="delete" data-id="${escapeHtml(id)}">Delete</button>
      </li>`;
  }

  #pager(): string {
    const showPager = this.#page > 1 || this.#hasNextPage;
    if (!showPager) return "";
    return `
      <div class="pager">
        <button data-action="prev" ${this.#page <= 1 ? "disabled" : ""}>← Prev</button>
        <span class="count">Page ${this.#page}</span>
        <button data-action="next" ${this.#hasNextPage ? "" : "disabled"}>Next →</button>
      </div>`;
  }

  protected override afterRender(): void {
    this.query<HTMLButtonElement>('[data-action="refresh"]')?.addEventListener(
      "click",
      () => void this.load(),
    );

    this.query<HTMLFormElement>("form.new")?.addEventListener("submit", (event) => {
      event.preventDefault();
      const input = this.query<HTMLInputElement>('form.new input[name="text"]');
      const text = input?.value ?? "";
      if (text.trim()) void this.create(text.trim());
    });

    for (const button of this.queryAll<HTMLButtonElement>("button[data-action]")) {
      const action = button.dataset.action;
      const id = button.dataset.id ?? "";
      switch (action) {
        case "edit":
          button.addEventListener("click", () => {
            this.#editingId = id;
            this.render();
          });
          break;
        case "cancel":
          button.addEventListener("click", () => {
            this.#editingId = null;
            this.render();
          });
          break;
        case "save":
          button.addEventListener("click", () => {
            const input = this.query<HTMLInputElement>(`[data-edit-input="${cssEscape(id)}"]`);
            if (input) void this.saveEdit(id, input.value.trim());
          });
          break;
        case "delete":
          button.addEventListener("click", () => void this.deleteMessage(id));
          break;
        case "prev":
          button.addEventListener("click", () => this.#goToPage(this.#page - 1));
          break;
        case "next":
          button.addEventListener("click", () => this.#goToPage(this.#page + 1));
          break;
        default:
          break;
      }
    }
  }
}

/**
 * `PageOfMessage.totalCount` arrives as an `UntypedNode` because the OpenAPI schema types
 * the counts as `integer | string` (an Olve.Results pagination quirk). Read its value
 * defensively and coerce to a number.
 */
function untypedToNumber(node: unknown): number {
  const value = (node as { getValue?: () => unknown } | null | undefined)?.getValue?.();
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/** Turn a thrown Kiota error into a human-readable line, calling out the auth case. */
function describeError(error: unknown): string {
  const status = (error as { responseStatusCode?: number } | undefined)?.responseStatusCode;
  if (status === 401 || status === 403) {
    return "Authentication required — creating, editing and deleting need a bearer token (see “Authenticated writes”).";
  }
  const problem = error as { messageEscaped?: string; message?: string } | undefined;
  return problem?.messageEscaped || problem?.message || "Request failed.";
}

/** Minimal CSS.escape fallback for attribute-selector-safe ids (GUIDs are already safe). */
function cssEscape(value: string): string {
  return typeof CSS !== "undefined" && CSS.escape ? CSS.escape(value) : value;
}
