import type { Skill } from "@arm/client";
import { escapeHtml } from "../base-element.js";
import { defaultSkills, skillsField } from "./skill-picks.js";

/**
 * The composer's skills (M10b): the `skills` button after tools in the settings row and the panel
 * under the row, in the page's light DOM so the page's styles apply (the tools panel's). The panel
 * holds one checkbox per registered skill, the ones flagged `default` ticked to start with; with
 * none registered it says so in one line.
 *
 * Ticking never rebuilds the markup (that would lose focus): the rows are updated in place, and
 * the panel is only re-rendered when the list changes.
 */
export class SkillPanel {
  /** The registered skills (`GET /api/skills`); `undefined` while there's no list. */
  #skills: Skill[] | undefined;
  #picked = new Set<string>();
  /** Whether the pick was changed by hand since the defaults (a new list then keeps it). */
  #touched = false;
  #open = false;

  constructor(
    readonly toggle: HTMLButtonElement,
    readonly panel: HTMLElement,
  ) {
    toggle.addEventListener("click", () => this.setOpen(!this.#open));
    panel.addEventListener("change", (e) => this.#change(e));
    panel.addEventListener("keydown", (e) => this.#keydown(e));
    this.render();
  }

  get open(): boolean {
    return this.#open;
  }

  /** The picked skills' names, in the list's order. */
  get picked(): string[] {
    return (this.#skills ?? []).filter((s) => this.#picked.has(s.name)).map((s) => s.name);
  }

  /** The create request's `skills`: left out for the registered defaults, `[]` for none. */
  get field(): string[] | undefined {
    return skillsField(this.#skills, this.#picked);
  }

  /**
   * A new list (`undefined`: none, as when signed out). An untouched pick starts again from its
   * defaults; a touched one keeps the skills that are still listed.
   */
  setSkills(skills: readonly Skill[] | undefined): void {
    this.#skills = skills ? [...skills] : undefined;
    if (!skills) this.#touched = false;
    if (!this.#touched) this.#picked = new Set(defaultSkills(this.#skills ?? []));
    else {
      const names = new Set(this.#skills?.map((s) => s.name));
      this.#picked = new Set([...this.#picked].filter((n) => names.has(n)));
    }
    this.render();
  }

  /** Opens or folds the panel. */
  setOpen(open: boolean): void {
    this.#open = open;
    this.panel.hidden = !open;
    this.refresh();
  }

  /** After a session is created: the registered defaults again. */
  clear(): void {
    this.#touched = false;
    this.#picked = new Set(defaultSkills(this.#skills ?? []));
    this.refresh();
  }

  /** Rebuilds the panel (the list changed). */
  render(): void {
    const rows = (this.#skills ?? [])
      .map(
        (s) =>
          `<label class="tool-pick"><input type="checkbox" data-pick="${escapeHtml(s.name)}">` +
          `<span class="name">${escapeHtml(s.name)}</span><span class="desc">${escapeHtml(s.description)}</span></label>`,
      )
      .join("");
    const none =
      this.#skills?.length === 0 ? `<p class="defaults none">No skills registered.</p>` : "";
    this.panel.innerHTML = `<div class="tool-rows">${rows || none}</div>`;
    this.refresh();
  }

  /** Updates what ticking changes, in place: the boxes, the button. */
  refresh(): void {
    for (const box of this.panel.querySelectorAll<HTMLInputElement>("[data-pick]")) {
      box.checked = this.#picked.has(box.dataset.pick ?? "");
      box.closest(".tool-pick")?.classList.toggle("on", box.checked);
    }
    const count = this.picked.length;
    this.toggle.setAttribute("aria-expanded", String(this.#open));
    this.toggle.title = this.#open ? "Hide this session's skills" : "Show this session's skills";
    this.toggle.innerHTML = `skills${count ? ` <span class="count">${count}</span>` : ""} <span class="caret">${this.#open ? "▾" : "▸"}</span>`;
  }

  #change(e: Event): void {
    const box = e.target as HTMLInputElement;
    const name = box.dataset.pick;
    if (!name) return;
    if (box.checked) this.#picked.add(name);
    else this.#picked.delete(name);
    this.#touched = true;
    this.refresh();
  }

  /** Enter on a checkbox doesn't start the session (the form's implicit submit); Ctrl+Enter does. */
  #keydown(e: KeyboardEvent): void {
    if (e.key !== "Enter" || !(e.target instanceof HTMLInputElement)) return;
    e.preventDefault();
    if (e.ctrlKey || e.metaKey) this.panel.closest("form")?.requestSubmit();
  }
}
