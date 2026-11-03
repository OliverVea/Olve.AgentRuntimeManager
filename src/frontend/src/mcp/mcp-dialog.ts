import type { McpServer } from "@arm/client";
import { ApiError, describeError } from "../api-errors.js";
import { escapeHtml } from "../base-element.js";
import { clock, relative } from "../sessions/format.js";
import {
  checkDraft,
  draftOf,
  type McpDraft,
  type McpField,
  mcpNameProblem,
  sourceText,
} from "./mcp-draft.js";
import type { McpRegistry } from "./mcp-registry.js";

const icon = (name: string) => `<svg class="i" aria-hidden="true"><use href="#i-${name}"/></svg>`;

const draftFields = ["description", "command", "args", "env"] as const;
const formFields = ["name", ...draftFields] as const;

/** The field a refusal from the server is about, by its code. */
const refusedFields: Record<string, McpField> = {
  RESERVED_ENV_NAME: "env",
  RESERVED_MCP_SERVER_NAME: "name",
};
const refusedField = (error: unknown): McpField | undefined =>
  error instanceof ApiError && error.code ? refusedFields[error.code] : undefined;

/**
 * The MCP servers dialog (M10c), opened from Options: the registered servers (name, description,
 * when it was updated, edit in place, delete with a confirm in the row; under them the command line
 * and the env's names) and a folding form to register one. In the page's light DOM, next to the
 * other dialogs, styled as the Skills dialog. The list is re-rendered when it changes; typing in
 * the form or in a row being edited only updates the flags in place.
 */
export class McpDialog {
  /** The servers as the list shows them (rows point into this by index). */
  #shown: McpServer[] = [];
  /** The row being edited: the server as it was when the edit began, the draft, whether it's being saved. */
  #editing: { name: string; original: McpServer; draft: McpDraft; saving: boolean } | undefined;
  /** The row asking whether to delete. */
  #confirming: string | undefined;
  /** What went wrong with the last change from the list. */
  #problem = "";
  /** The edited field the server's refusal is about. */
  #problemField: McpField | undefined;
  /** Whether a register is in flight (Register stays disabled until it settles). */
  #registering = false;
  /** Why the server refused the last register; kept until the form is typed in again. */
  #registerProblem = "";
  /** The field the server's refusal is about. */
  #registerBad: McpField | undefined;

  constructor(
    readonly dialog: HTMLDialogElement,
    readonly registry: McpRegistry,
    readonly now: () => number = Date.now,
  ) {
    dialog.classList.add("env-dialog", "mcp-dialog");
    dialog.innerHTML = `<form method="dialog">
      <h3>MCP servers</h3>
      <p>Servers a session can name in its <b>tools</b>; its agent gets all of their tools. A session keeps the definition it was created with.</p>
      <div class="env-list" data-list></div>
      <p class="problem" data-problem></p>
      <details class="env-form fold" data-register-form>
        <summary>Register a server</summary>
        <div class="fields">
          <label class="field">name <input data-register="name" placeholder="github" spellcheck="false" autocomplete="off" /></label>
          <label class="field">description <input data-register="description" placeholder="What it's for, in one line" spellcheck="false" autocomplete="off" /></label>
          <label class="field">command <input data-register="command" placeholder="npx" spellcheck="false" autocomplete="off" /></label>
          <label class="field">arguments <textarea data-register="args" rows="2" spellcheck="false" placeholder="one per line"></textarea></label>
          <label class="field wide">env <textarea data-register="env" rows="2" spellcheck="false" placeholder="NAME=value, one per line"></textarea></label>
        </div>
        <p class="err" data-register-err hidden></p>
        <div class="foot"><span class="spacer"></span><button type="button" class="primary" data-register-submit disabled>Register</button></div>
      </details>
      <div class="buttons"><button type="button" class="ghost" data-close>Close</button></div>
    </form>`;
    // Nothing here submits: Enter in a field must not close the dialog.
    this.#form.addEventListener("submit", (e) => e.preventDefault());
    dialog.addEventListener("click", (e) => void this.#click(e));
    dialog.addEventListener("input", (e) => this.#input(e));
    dialog.addEventListener("keydown", (e) => this.#keydown(e));
    registry.addEventListener("change", () => this.#registryChanged());
  }

  /** Opens the dialog and fetches the list. */
  open(): void {
    this.#editing = undefined;
    this.#confirming = undefined;
    this.#problem = "";
    this.#problemField = undefined;
    this.#registerForm.open = this.registry.loaded && this.registry.servers.length === 0;
    this.render();
    this.checkRegister();
    if (!this.dialog.open) this.dialog.showModal();
    void this.registry.load();
  }

  /** Rebuilds the list (it changed, or a row starts or stops editing or confirming). */
  render(): void {
    const active = document.activeElement;
    const focus =
      (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) &&
      active.dataset.edit !== undefined
        ? { field: active.dataset.edit, start: active.selectionStart, end: active.selectionEnd }
        : undefined;
    this.#shown = this.registry.servers;
    const now = this.now();
    const list = this.#shown.map((s, i) => this.#item(s, i, now)).join("");
    this.#el("[data-list]").innerHTML =
      list ||
      (this.registry.loaded
        ? `<div class="env-empty">No MCP servers registered.</div>`
        : this.registry.problem
          ? ""
          : `<div class="env-empty">Loading…</div>`);
    this.checkEdit();
    if (focus) {
      const field = this.dialog.querySelector<HTMLInputElement | HTMLTextAreaElement>(
        `[data-edit="${focus.field}"]`,
      );
      field?.focus();
      field?.setSelectionRange(focus.start, focus.end);
    }
  }

  /**
   * Flags the row being edited in place and enables Save when it can be sent, and shows the
   * problem under the list: the edit's, the last change's, or the fetch's.
   */
  checkEdit(): void {
    const editing = this.#editing;
    const check = editing ? checkDraft(editing.draft, editing.original) : undefined;
    const bad = this.#problem ? this.#problemField : check?.bad;
    for (const field of draftFields) {
      this.dialog.querySelector(`[data-edit="${field}"]`)?.classList.toggle("bad", bad === field);
    }
    const save = this.dialog.querySelector<HTMLButtonElement>("[data-save]");
    if (save) save.disabled = !!editing?.saving || !check?.body;
    this.#el("[data-problem]").textContent =
      this.#problem ||
      check?.problem ||
      (this.registry.problem ? `Couldn't load the MCP servers: ${this.registry.problem}` : "");
  }

  /**
   * Flags the register form's fields in place and enables Register when it can be sent: a name
   * (checked as the server does, and not one that's registered), a description, a command, and
   * env lines that parse. While a register is in flight, Register stays disabled and the flags
   * stay as they are.
   */
  checkRegister(): void {
    const button = this.#el<HTMLButtonElement>("[data-register-submit]");
    if (this.#registering) {
      button.disabled = true;
      return;
    }
    const name = this.#registerField("name").value.trim();
    const nameProblem = name
      ? (mcpNameProblem(name) ??
        (this.registry.get(name) ? `${name} is already registered: change it in the list.` : ""))
      : "";
    const check = checkDraft(this.#registerDraft());
    const serverProblem = this.#registerProblem;
    const problem = serverProblem || nameProblem || check.problem;
    const bad = serverProblem ? this.#registerBad : nameProblem ? "name" : check.bad;
    for (const field of formFields)
      this.#registerField(field).classList.toggle("bad", bad === field);
    const err = this.#el("[data-register-err]");
    err.textContent = problem;
    err.hidden = !problem;
    button.disabled = !serverProblem && (!name || !!nameProblem || !check.body);
  }

  #item(s: McpServer, i: number, now: number): string {
    const name = escapeHtml(s.name);
    const description = escapeHtml(s.description);
    const source = escapeHtml(sourceText(s));
    const updated = `<span class="updated" title="${escapeHtml(clock(s.updatedAt, now))}">${escapeHtml(relative(s.updatedAt, now))}</span>`;
    if (this.#editing?.name === s.name) {
      const draft = this.#editing.draft;
      return (
        `<div class="env-item editing" data-i="${i}"><span class="edit-fields">` +
        `<label class="field">name <input value="${name}" disabled /></label>` +
        `<label class="field">description <input data-edit="description" value="${escapeHtml(draft.description)}" spellcheck="false" autocomplete="off" /></label>` +
        `<label class="field">command <input data-edit="command" value="${escapeHtml(draft.command)}" spellcheck="false" autocomplete="off" /></label>` +
        `<label class="field">arguments <textarea data-edit="args" rows="2" spellcheck="false">${escapeHtml(draft.args)}</textarea></label>` +
        `<label class="field wide">env <textarea data-edit="env" rows="2" spellcheck="false" placeholder="NAME=value">${escapeHtml(draft.env)}</textarea></label>` +
        `</span><span></span><span></span><span class="acts edit-acts"><button type="button" class="ghost" data-cancel>Cancel</button><button type="button" class="primary" data-save>Save</button></span></div>`
      );
    }
    const src = `<span class="src" title="${source}">${source}</span>`;
    if (this.#confirming === s.name) {
      return `<div class="env-item confirming" data-i="${i}"><span class="name">${name}</span><span class="value desc" title="${description}">${description}</span>${updated}<span class="acts"></span>${src}<div class="confirm"><span>Delete ${name}? New sessions can't name it; existing ones keep theirs.</span><button type="button" class="ghost" data-keep>Keep</button><button type="button" class="primary danger" data-confirm-delete>Delete</button></div></div>`;
    }
    return `<div class="env-item" data-i="${i}"><span class="name">${name}</span><span class="value desc" title="${description}">${description}</span>${updated}<span class="acts"><button type="button" data-edit-row title="Edit" aria-label="Edit ${name}">${icon("pencil")}</button><button type="button" class="del" data-delete title="Delete" aria-label="Delete ${name}">${icon("trash")}</button></span>${src}</div>`;
  }

  #registryChanged(): void {
    // A row being edited or confirmed that's gone (deleted elsewhere) stops being so.
    if (this.#editing && !this.registry.get(this.#editing.name)) this.#editing = undefined;
    if (this.#confirming && !this.registry.get(this.#confirming)) this.#confirming = undefined;
    if (this.registry.loaded && this.registry.servers.length === 0) this.#registerForm.open = true;
    this.render();
    this.checkRegister();
  }

  /** The server a list row stands for. */
  #serverOf(target: HTMLElement): McpServer | undefined {
    const row = target.closest<HTMLElement>(".env-item");
    return row ? this.#shown[Number(row.dataset.i)] : undefined;
  }

  async #click(e: Event): Promise<void> {
    const target = e.target as HTMLElement;
    if (target.closest("[data-register-submit]")) return this.#register();
    const server = this.#serverOf(target);
    if (!server) return;
    if (target.closest("[data-edit-row]")) {
      this.#editing = {
        name: server.name,
        original: server,
        draft: draftOf(server),
        saving: false,
      };
      this.#confirming = undefined;
      this.#clearProblem();
      this.render();
      this.dialog.querySelector<HTMLInputElement>('[data-edit="description"]')?.focus();
    } else if (target.closest("[data-cancel]")) {
      this.#editing = undefined;
      this.#clearProblem();
      this.render();
    } else if (target.closest("[data-save]")) {
      await this.#save();
    } else if (target.closest("[data-delete]")) {
      this.#confirming = server.name;
      this.#editing = undefined;
      this.#clearProblem();
      this.render();
    } else if (target.closest("[data-keep]")) {
      this.#confirming = undefined;
      this.render();
    } else if (target.closest("[data-confirm-delete]")) {
      const button = target.closest("button");
      if (button) button.disabled = true;
      await this.#run(() => this.registry.remove(server.name));
      if (!this.#problem) this.#confirming = undefined;
      this.render();
    }
  }

  #input(e: Event): void {
    const field = e.target as HTMLInputElement | HTMLTextAreaElement;
    const edited = field.dataset.edit as (typeof draftFields)[number] | undefined;
    if (edited && this.#editing) {
      this.#editing.draft[edited] = field.value;
      this.#clearProblem();
      this.checkEdit();
    } else if (field.dataset.register !== undefined) {
      this.#registerProblem = "";
      this.#registerBad = undefined;
      this.checkRegister();
    }
  }

  /** Enter in a one-line field never submits the dialog's form: it registers, or saves an edit. */
  #keydown(e: KeyboardEvent): void {
    if (e.key !== "Enter" || !(e.target instanceof HTMLInputElement)) return;
    e.preventDefault();
    const input = e.target;
    if (input.dataset.edit !== undefined) {
      if (!this.dialog.querySelector<HTMLButtonElement>("[data-save]")?.disabled) void this.#save();
    } else if (input.dataset.register !== undefined) {
      if (!this.#el<HTMLButtonElement>("[data-register-submit]").disabled) void this.#register();
    }
  }

  /**
   * Saves the row being edited: the whole server replaced with what its fields hold. Save stays
   * disabled until it settles; an edit begun on another row meanwhile is left open.
   */
  async #save(): Promise<void> {
    const editing = this.#editing;
    const body = editing && !editing.saving && checkDraft(editing.draft, editing.original).body;
    if (!editing || !body) return;
    editing.saving = true;
    this.checkEdit();
    await this.#run(() => this.registry.set(editing.name, body));
    editing.saving = false;
    if (!this.#problem && this.#editing === editing) this.#editing = undefined;
    this.render();
  }

  async #register(): Promise<void> {
    const name = this.#registerField("name").value.trim();
    const body = checkDraft(this.#registerDraft()).body;
    if (this.#registering || !name || mcpNameProblem(name) || this.registry.get(name) || !body) {
      return;
    }
    this.#registering = true;
    this.#registerProblem = "";
    this.#registerBad = undefined;
    this.checkRegister();
    try {
      await this.registry.set(name, body);
      for (const field of formFields) this.#registerField(field).value = "";
      this.#registerField("name").focus();
    } catch (error) {
      this.#registerProblem = describeError(error);
      this.#registerBad = refusedField(error);
    } finally {
      this.#registering = false;
      this.checkRegister();
    }
  }

  /** Runs a change from the list, keeping its error (and the field it's about) to show under the list. */
  async #run(action: () => Promise<void>): Promise<void> {
    this.#clearProblem();
    try {
      await action();
    } catch (error) {
      this.#problem = describeError(error);
      this.#problemField = refusedField(error);
    }
  }

  #clearProblem(): void {
    this.#problem = "";
    this.#problemField = undefined;
  }

  #registerDraft(): McpDraft {
    return {
      description: this.#registerField("description").value,
      command: this.#registerField("command").value,
      args: this.#registerField("args").value,
      env: this.#registerField("env").value,
    };
  }

  get #form(): HTMLFormElement {
    return this.#el<HTMLFormElement>("form");
  }

  get #registerForm(): HTMLDetailsElement {
    return this.#el<HTMLDetailsElement>("[data-register-form]");
  }

  #registerField(field: McpField): HTMLInputElement | HTMLTextAreaElement {
    return this.#el<HTMLInputElement | HTMLTextAreaElement>(`[data-register="${field}"]`);
  }

  #el<E extends HTMLElement = HTMLElement>(selector: string): E {
    const el = this.dialog.querySelector<E>(selector);
    if (!el) throw new Error(`missing ${selector}`);
    return el;
  }
}
