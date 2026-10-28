import type { Skill, SkillSource } from "@arm/client";
import { ApiError, describeError } from "../api-errors.js";
import { escapeHtml } from "../base-element.js";
import { clock, relative } from "../sessions/format.js";
import { skillNameProblem } from "./skill-names.js";
import type { SkillRegistry } from "./skill-registry.js";

const icon = (name: string) => `<svg class="i" aria-hidden="true"><use href="#i-${name}"/></svg>`;

/** The install form's text fields. */
type InstallField = "name" | "url" | "ref" | "path";

/** A skill's source in one line: `url#ref path`. */
export function sourceText(source: SkillSource): string {
  return `${source.url}${source.ref ? `#${source.ref}` : ""}${source.path ? ` ${source.path}` : ""}`;
}

/**
 * The Skills dialog, opened from Options: the registered skills (name, description, a "default"
 * switch that saves at once, when it was updated, where it came from, delete with a confirm in
 * the row) and a folding form to install one from a git repository. In the page's light DOM, next
 * to the other dialogs, styled as the Environment variables dialog. The list is re-rendered when
 * it changes; typing in the form only updates its flags in place.
 */
export class SkillDialog {
  /** The skills as the list shows them (rows point into this by index). */
  #shown: Skill[] = [];
  /** The row asking whether to delete. */
  #confirming: string | undefined;
  /** What went wrong with the last change from the list. */
  #problem = "";
  /** Whether an install is in flight (Install stays disabled until it settles). */
  #installing = false;
  /** Why the server refused the last install; kept until the form is typed in again. */
  #installProblem = "";
  /** The field the server's refusal is about (the repository, when it couldn't be fetched). */
  #installBad: InstallField | undefined;

  constructor(
    readonly dialog: HTMLDialogElement,
    readonly registry: SkillRegistry,
    readonly now: () => number = Date.now,
  ) {
    dialog.classList.add("env-dialog", "skills-dialog");
    dialog.innerHTML = `<form method="dialog">
      <h3>Skills</h3>
      <p>What agents can load with <code>arm_skill_load</code>. <b>Default</b> ones go to every session that picks none. A session copies its skills when it starts.</p>
      <div class="env-list" data-list></div>
      <p class="problem" data-problem></p>
      <details class="env-form fold" data-install-form>
        <summary>Install a skill</summary>
        <div class="fields">
          <label class="field">name <input data-install="name" placeholder="pdf" spellcheck="false" autocomplete="off" /></label>
          <label class="field">repository <input data-install="url" placeholder="https://github.com/anthropics/skills" spellcheck="false" autocomplete="off" /></label>
          <label class="field">ref <input data-install="ref" placeholder="default branch" spellcheck="false" autocomplete="off" /></label>
          <label class="field">path <input data-install="path" placeholder="skills/pdf" spellcheck="false" autocomplete="off" /></label>
        </div>
        <p class="err" data-install-err hidden></p>
        <div class="foot"><label class="switch"><input type="checkbox" data-install="default" /> default</label><span class="spacer"></span><button type="button" class="primary" data-install-submit disabled>Install</button></div>
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
    this.#confirming = undefined;
    this.#problem = "";
    this.#installForm.open = this.registry.loaded && this.registry.skills.length === 0;
    this.render();
    this.checkInstall();
    if (!this.dialog.open) this.dialog.showModal();
    void this.registry.load();
  }

  /** Rebuilds the list (it changed, or a row starts or stops confirming). */
  render(): void {
    this.#shown = this.registry.skills;
    const now = this.now();
    const list = this.#shown.map((s, i) => this.#item(s, i, now)).join("");
    this.#el("[data-list]").innerHTML =
      list ||
      (this.registry.loaded
        ? `<div class="env-empty">No skills registered.</div>`
        : this.registry.problem
          ? ""
          : `<div class="env-empty">Loading…</div>`);
    this.#el("[data-problem]").textContent =
      this.#problem ||
      (this.registry.problem ? `Couldn't load the skills: ${this.registry.problem}` : "");
  }

  /**
   * Flags the install form's fields in place and enables Install when it can be sent: a name
   * (checked as the server does) and a repository. While an install is in flight, Install stays
   * disabled and the flags stay as they are.
   */
  checkInstall(): void {
    const button = this.#el<HTMLButtonElement>("[data-install-submit]");
    if (this.#installing) {
      button.disabled = true;
      return;
    }
    const name = this.#installInput("name").value.trim();
    const url = this.#installInput("url").value.trim();
    const serverProblem = this.#installProblem;
    const nameProblem = name ? skillNameProblem(name) : undefined;
    const problem = serverProblem || nameProblem || "";
    for (const field of ["name", "url", "ref", "path"] as const) {
      const bad = serverProblem ? this.#installBad === field : field === "name" && !!nameProblem;
      this.#installInput(field).classList.toggle("bad", bad);
    }
    const err = this.#el("[data-install-err]");
    err.textContent = problem;
    err.hidden = !problem;
    button.disabled = !name || !url || (!!nameProblem && !serverProblem);
  }

  #item(s: Skill, i: number, now: number): string {
    const name = escapeHtml(s.name);
    const description = escapeHtml(s.description);
    const source = escapeHtml(sourceText(s.source));
    const updated = `<span class="updated" title="${escapeHtml(clock(s.updatedAt, now))}">${escapeHtml(relative(s.updatedAt, now))}</span>`;
    const isDefault = `<label class="switch"><input type="checkbox" data-default${s.default ? " checked" : ""} /> default</label>`;
    const src = `<span class="src" title="${source}">${source}</span>`;
    if (this.#confirming === s.name) {
      return `<div class="env-item confirming" data-i="${i}"><span class="name">${name}</span><span class="value desc" title="${description}">${description}</span>${isDefault}${updated}<span class="acts"></span>${src}<div class="confirm"><span>Delete ${name}? New sessions stop getting it; running ones keep their copy.</span><button type="button" class="ghost" data-keep>Keep</button><button type="button" class="primary danger" data-confirm-delete>Delete</button></div></div>`;
    }
    return `<div class="env-item" data-i="${i}"><span class="name">${name}</span><span class="value desc" title="${description}">${description}</span>${isDefault}${updated}<span class="acts"><button type="button" class="del" data-delete title="Delete" aria-label="Delete ${name}">${icon("trash")}</button></span>${src}</div>`;
  }

  #registryChanged(): void {
    // A row being confirmed that's gone (deleted elsewhere) stops being so.
    if (this.#confirming && !this.registry.get(this.#confirming)) this.#confirming = undefined;
    if (this.registry.loaded && this.registry.skills.length === 0) this.#installForm.open = true;
    this.render();
    this.checkInstall();
  }

  /** The skill a list row stands for. */
  #skillOf(target: HTMLElement): Skill | undefined {
    const row = target.closest<HTMLElement>(".env-item");
    return row ? this.#shown[Number(row.dataset.i)] : undefined;
  }

  async #click(e: Event): Promise<void> {
    const target = e.target as HTMLElement;
    const skill = this.#skillOf(target);
    if (target.closest("[data-install-submit]")) return this.#install();
    if (!skill) return;
    if (target.closest("[data-delete]")) {
      this.#confirming = skill.name;
      this.#problem = "";
      this.render();
    } else if (target.closest("[data-keep]")) {
      this.#confirming = undefined;
      this.render();
    } else if (target.closest("[data-confirm-delete]")) {
      const button = target.closest("button");
      if (button) button.disabled = true;
      await this.#run(() => this.registry.remove(skill.name));
      if (!this.#problem) this.#confirming = undefined;
      this.render();
    }
  }

  /** The "default" switch of a row saves at once: the skill installed again from its source. */
  async #change(e: Event): Promise<void> {
    const box = e.target as HTMLInputElement;
    const skill = this.#skillOf(box);
    if (box.dataset.default === undefined || !skill) return;
    box.disabled = true;
    await this.#run(() => this.registry.setDefault(skill.name, box.checked));
    this.render(); // the list as the server has it (the switch back, if it failed)
  }

  #input(e: Event): void {
    const input = e.target as HTMLInputElement;
    if (input.dataset.install !== undefined && input.dataset.install !== "default") {
      this.#installProblem = "";
      this.#installBad = undefined;
      this.checkInstall();
    }
  }

  /** Enter in a field never submits the dialog's form: it installs. */
  #keydown(e: KeyboardEvent): void {
    if (e.key !== "Enter" || !(e.target instanceof HTMLInputElement)) return;
    e.preventDefault();
    const input = e.target;
    if (input.dataset.install !== undefined && input.dataset.install !== "default") {
      if (!this.#el<HTMLButtonElement>("[data-install-submit]").disabled) void this.#install();
    }
  }

  /** Sends the form: a skill of that name that exists is replaced (the API's PUT). */
  async #install(): Promise<void> {
    const name = this.#installInput("name").value.trim();
    const url = this.#installInput("url").value.trim();
    if (this.#installing || !name || !url || skillNameProblem(name)) return;
    const ref = this.#installInput("ref").value.trim();
    const path = this.#installInput("path").value.trim();
    const source: SkillSource = { url };
    if (ref) source.ref = ref;
    if (path) source.path = path;
    this.#installing = true;
    this.#installProblem = "";
    this.#installBad = undefined;
    this.checkInstall();
    try {
      await this.registry.install(name, {
        source,
        default: this.#installInput("default").checked,
      });
      for (const field of ["name", "url", "ref", "path"] as const)
        this.#installInput(field).value = "";
      this.#installInput("default").checked = false;
      this.#installInput("name").focus();
    } catch (error) {
      this.#installProblem = describeError(error);
      this.#installBad =
        error instanceof ApiError && error.code === "SKILL_SOURCE_FAILED" ? "url" : undefined;
    } finally {
      this.#installing = false;
      this.checkInstall();
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

  get #installForm(): HTMLDetailsElement {
    return this.#el<HTMLDetailsElement>("[data-install-form]");
  }

  #installInput(part: InstallField | "default"): HTMLInputElement {
    return this.#el<HTMLInputElement>(`[data-install="${part}"]`);
  }

  #el<E extends HTMLElement = HTMLElement>(selector: string): E {
    const el = this.dialog.querySelector<E>(selector);
    if (!el) throw new Error(`missing ${selector}`);
    return el;
  }
}
