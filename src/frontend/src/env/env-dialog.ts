import type { EnvVariable } from "@arm/client";
import { describeError } from "../api-errors.js";
import { escapeHtml } from "../base-element.js";
import { clock, relative } from "../sessions/format.js";
import { envNameProblem } from "./env-names.js";
import type { EnvRegistry } from "./env-registry.js";

const icon = (name: string) => `<svg class="i" aria-hidden="true"><use href="#i-${name}"/></svg>`;

/**
 * The Environment variables dialog (approved mock: mocks/env.html), opened from Options: the
 * registered variables (name, value, an "every agent" switch that saves at once, when it was
 * updated, edit in place, delete with a confirm in the row) and a folding form to add one. In the
 * page's light DOM, next to the other dialogs. The list is re-rendered when it changes; typing in
 * the form or an edited value only updates the flags in place.
 */
export class EnvDialog {
  /** The variables as the list shows them (rows point into this by index). */
  #shown: EnvVariable[] = [];
  /** The row being edited, with its draft. */
  #editing: { name: string; value: string; default: boolean } | undefined;
  /** The row asking whether to delete. */
  #confirming: string | undefined;
  /** What went wrong with the last change from the list. */
  #problem = "";

  constructor(
    readonly dialog: HTMLDialogElement,
    readonly registry: EnvRegistry,
    readonly now: () => number = Date.now,
  ) {
    dialog.classList.add("env-dialog");
    dialog.innerHTML = `<form method="dialog">
      <h3>Environment variables</h3>
      <p>What agents get in their environment. <b>Every agent</b> ones go to all; the others only to sessions that pick them. A session takes the values when it's created.</p>
      <div class="env-list" data-list></div>
      <p class="problem" data-problem></p>
      <details class="env-form fold" data-add-form>
        <summary>Add a variable</summary>
        <div class="fields">
          <label class="field">name <input data-add="name" placeholder="NAME" spellcheck="false" autocomplete="off" /></label>
          <label class="field">value <input data-add="value" placeholder="value" spellcheck="false" autocomplete="off" /></label>
        </div>
        <p class="err" data-add-err hidden></p>
        <div class="foot"><label class="switch"><input type="checkbox" data-add="default" /> every agent</label><span class="spacer"></span><button type="button" class="primary" data-add-submit disabled>Add</button></div>
        <p class="not-secret">⚠ Not secret: anyone who can use ARM reads these values back. Don't put tokens or passwords here.</p>
      </details>
      <div class="buttons"><button type="button" class="ghost" data-close>Close</button></div>
    </form>`;
    // Nothing here submits: Enter in a field must not close the dialog.
    this.#form.addEventListener("submit", (e) => e.preventDefault());
    dialog.addEventListener("click", (e) => void this.#click(e));
    dialog.addEventListener("change", (e) => void this.#change(e));
    dialog.addEventListener("input", (e) => this.#input(e));
    dialog.addEventListener("keydown", (e) => this.#keydown(e));
    registry.addEventListener("change", () => this.#registryChanged());
  }

  /** Opens the dialog and fetches the list. */
  open(): void {
    this.#editing = undefined;
    this.#confirming = undefined;
    this.#problem = "";
    this.#addForm.open = this.registry.loaded && this.registry.variables.length === 0;
    this.render();
    this.checkAdd();
    if (!this.dialog.open) this.dialog.showModal();
    void this.registry.load();
  }

  /** Rebuilds the list (it changed, or a row starts or stops editing or confirming). */
  render(): void {
    const active = document.activeElement;
    const editFocus =
      active instanceof HTMLInputElement && active.dataset.editValue !== undefined
        ? { start: active.selectionStart, end: active.selectionEnd }
        : undefined;
    this.#shown = this.registry.variables;
    const now = this.now();
    const list = this.#shown.map((v, i) => this.#item(v, i, now)).join("");
    this.#el("[data-list]").innerHTML =
      list ||
      (this.registry.loaded
        ? `<div class="env-empty">None yet. Add one below, e.g. your git identity for agents that commit.</div>`
        : this.registry.problem
          ? ""
          : `<div class="env-empty">Loading…</div>`);
    this.#el("[data-problem]").textContent =
      this.#problem ||
      (this.registry.problem ? `Couldn't load the variables: ${this.registry.problem}` : "");
    if (editFocus) {
      const input = this.dialog.querySelector<HTMLInputElement>("[data-edit-value]");
      input?.focus();
      input?.setSelectionRange(editFocus.start, editFocus.end);
    }
  }

  /** Flags the Add form's name in place and enables Add when it can be sent. */
  checkAdd(serverProblem = ""): void {
    const input = this.#addInput("name");
    const name = input.value.trim();
    const problem =
      serverProblem ||
      (name
        ? (envNameProblem(name, "registered") ??
          (this.registry.get(name) ? `${name} is already registered: change it in the list.` : ""))
        : "");
    input.classList.toggle("bad", !!problem);
    const err = this.#el("[data-add-err]");
    err.textContent = problem;
    err.hidden = !problem;
    this.#el<HTMLButtonElement>("[data-add-submit]").disabled =
      !name || (!!problem && !serverProblem);
  }

  #item(v: EnvVariable, i: number, now: number): string {
    const name = escapeHtml(v.name);
    const value = escapeHtml(v.value);
    const updated = `<span class="updated" title="${escapeHtml(clock(v.updatedAt, now))}">${escapeHtml(relative(v.updatedAt, now))}</span>`;
    if (this.#editing?.name === v.name) {
      const draft = this.#editing;
      return `<div class="env-item editing" data-i="${i}"><span class="edit-fields"><label class="field">name <input value="${name}" disabled /></label><label class="field">value <input data-edit-value value="${escapeHtml(draft.value)}" spellcheck="false" autocomplete="off" /></label></span><label class="switch"><input type="checkbox" data-edit-default${draft.default ? " checked" : ""} /> every agent</label><span></span><span class="acts edit-acts"><button type="button" class="ghost" data-cancel>Cancel</button><button type="button" class="primary" data-save>Save</button></span></div>`;
    }
    const everyAgent = `<label class="switch"><input type="checkbox" data-default${v.default ? " checked" : ""} /> every agent</label>`;
    if (this.#confirming === v.name) {
      return `<div class="env-item confirming" data-i="${i}"><span class="name">${name}</span><span class="value" title="${value}">${value}</span>${everyAgent}${updated}<span class="acts"></span><div class="confirm"><span>Delete ${name}? New sessions stop getting it; existing ones keep their value.</span><button type="button" class="ghost" data-keep>Keep</button><button type="button" class="primary danger" data-confirm-delete>Delete</button></div></div>`;
    }
    return `<div class="env-item" data-i="${i}"><span class="name">${name}</span><span class="value" title="${value}">${value}</span>${everyAgent}${updated}<span class="acts"><button type="button" data-edit title="Edit" aria-label="Edit ${name}">${icon("pencil")}</button><button type="button" class="del" data-delete title="Delete" aria-label="Delete ${name}">${icon("trash")}</button></span></div>`;
  }

  #registryChanged(): void {
    // A row being edited or confirmed that's gone (deleted elsewhere) stops being so.
    if (this.#editing && !this.registry.get(this.#editing.name)) this.#editing = undefined;
    if (this.#confirming && !this.registry.get(this.#confirming)) this.#confirming = undefined;
    if (this.registry.loaded && this.registry.variables.length === 0) this.#addForm.open = true;
    this.render();
    this.checkAdd();
  }

  /** The variable a list row stands for. */
  #variableOf(target: HTMLElement): EnvVariable | undefined {
    const row = target.closest<HTMLElement>(".env-item");
    return row ? this.#shown[Number(row.dataset.i)] : undefined;
  }

  async #click(e: Event): Promise<void> {
    const target = e.target as HTMLElement;
    const variable = this.#variableOf(target);
    if (target.closest("[data-add-submit]")) return this.#add();
    if (!variable) return;
    if (target.closest("[data-edit]")) {
      this.#editing = { name: variable.name, value: variable.value, default: variable.default };
      this.#confirming = undefined;
      this.#problem = "";
      this.render();
      this.dialog.querySelector<HTMLInputElement>("[data-edit-value]")?.focus();
    } else if (target.closest("[data-cancel]")) {
      this.#editing = undefined;
      this.render();
    } else if (target.closest("[data-save]")) {
      await this.#save();
    } else if (target.closest("[data-delete]")) {
      this.#confirming = variable.name;
      this.#editing = undefined;
      this.#problem = "";
      this.render();
    } else if (target.closest("[data-keep]")) {
      this.#confirming = undefined;
      this.render();
    } else if (target.closest("[data-confirm-delete]")) {
      const button = target.closest("button");
      if (button) button.disabled = true;
      await this.#run(() => this.registry.remove(variable.name));
      if (!this.#problem) this.#confirming = undefined;
      this.render();
    }
  }

  /** The "every agent" switch of a row saves at once; in a row being edited it's part of the edit. */
  async #change(e: Event): Promise<void> {
    const box = e.target as HTMLInputElement;
    if (box.dataset.editDefault !== undefined && this.#editing) {
      this.#editing.default = box.checked;
      return;
    }
    const variable = this.#variableOf(box);
    if (box.dataset.default === undefined || !variable) return;
    box.disabled = true;
    await this.#run(() =>
      this.registry.set(variable.name, { value: variable.value, default: box.checked }),
    );
    this.render(); // the list as the server has it (the switch back, if it failed)
  }

  #input(e: Event): void {
    const input = e.target as HTMLInputElement;
    if (input.dataset.editValue !== undefined && this.#editing) this.#editing.value = input.value;
    else if (input.dataset.add === "name") this.checkAdd();
  }

  /** Enter in a field never submits the dialog's form: it adds, or saves an edit. */
  #keydown(e: KeyboardEvent): void {
    if (e.key !== "Enter" || !(e.target instanceof HTMLInputElement)) return;
    e.preventDefault();
    const input = e.target;
    if (input.dataset.editValue !== undefined) void this.#save();
    else if (input.dataset.add === "name" || input.dataset.add === "value") {
      if (!this.#el<HTMLButtonElement>("[data-add-submit]").disabled) void this.#add();
    }
  }

  async #save(): Promise<void> {
    const draft = this.#editing;
    if (!draft) return;
    await this.#run(() =>
      this.registry.set(draft.name, { value: draft.value, default: draft.default }),
    );
    if (!this.#problem) this.#editing = undefined;
    this.render();
  }

  async #add(): Promise<void> {
    const name = this.#addInput("name").value.trim();
    if (!name || envNameProblem(name, "registered") || this.registry.get(name)) return;
    const button = this.#el<HTMLButtonElement>("[data-add-submit]");
    button.disabled = true;
    try {
      await this.registry.set(name, {
        value: this.#addInput("value").value,
        default: this.#addInput("default").checked,
      });
      this.#addInput("name").value = "";
      this.#addInput("value").value = "";
      this.#addInput("default").checked = false;
      this.checkAdd();
      this.#addInput("name").focus();
    } catch (error) {
      this.checkAdd(describeError(error));
    }
  }

  /** Runs a change from the list, keeping its error to show under the list. */
  async #run(action: () => Promise<void>): Promise<void> {
    this.#problem = "";
    try {
      await action();
    } catch (error) {
      this.#problem = describeError(error);
    }
  }

  get #form(): HTMLFormElement {
    return this.#el<HTMLFormElement>("form");
  }

  get #addForm(): HTMLDetailsElement {
    return this.#el<HTMLDetailsElement>("[data-add-form]");
  }

  #addInput(part: "name" | "value" | "default"): HTMLInputElement {
    return this.#el<HTMLInputElement>(`[data-add="${part}"]`);
  }

  #el<E extends HTMLElement = HTMLElement>(selector: string): E {
    const el = this.dialog.querySelector<E>(selector);
    if (!el) throw new Error(`missing ${selector}`);
    return el;
  }
}
