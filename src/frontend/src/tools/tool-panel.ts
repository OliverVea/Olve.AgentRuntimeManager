import type { Tool } from "@arm/client";
import { escapeHtml } from "../base-element.js";
import { defaultTools, toolsField } from "./tool-picks.js";

/**
 * The composer's tools (M10): the `tools` button after env and repos in the settings row and the
 * panel under the row, in the page's light DOM so the page's styles apply (the env panel's). The
 * panel holds one checkbox per tool of ARM's, the server's defaults ticked to start with.
 *
 * Ticking never rebuilds the markup (that would lose focus): the rows are updated in place, and
 * the panel is only re-rendered when the list changes.
 */
export class ToolPanel {
  /** The tools listed (`GET /api/tools`); `undefined` while there's no list. */
  #tools: Tool[] | undefined;
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

  /** The picked tools' names, in the list's order. */
  get picked(): string[] {
    return (this.#tools ?? []).filter((t) => this.#picked.has(t.name)).map((t) => t.name);
  }

  /** The create request's `tools`: left out for the server's defaults, `[]` for none. */
  get field(): string[] | undefined {
    return toolsField(this.#tools, this.#picked);
  }

  /**
   * A new list (`undefined`: none, as when signed out). An untouched pick starts again from its
   * defaults; a touched one keeps the tools that are still listed.
   */
  setTools(tools: readonly Tool[] | undefined): void {
    this.#tools = tools ? [...tools] : undefined;
    if (!tools) this.#touched = false;
    if (!this.#touched) this.#picked = new Set(defaultTools(this.#tools ?? []));
    else {
      const names = new Set(this.#tools?.map((t) => t.name));
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

  /** After a session is created: the server's defaults again. */
  clear(): void {
    this.#touched = false;
    this.#picked = new Set(defaultTools(this.#tools ?? []));
    this.refresh();
  }

  /** Rebuilds the panel (the list changed). */
  render(): void {
    const rows = (this.#tools ?? [])
      .map(
        (t) =>
          `<label class="tool-pick"><input type="checkbox" data-pick="${escapeHtml(t.name)}">` +
          `<span class="name">${escapeHtml(t.name)}</span><span class="desc">${escapeHtml(t.description)}</span></label>`,
      )
      .join("");
    this.panel.innerHTML = `<div class="tool-rows">${rows}</div>`;
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
    this.toggle.title = this.#open ? "Hide this session's tools" : "Show this session's tools";
    this.toggle.innerHTML = `tools${count ? ` <span class="count">${count}</span>` : ""} <span class="caret">${this.#open ? "▾" : "▸"}</span>`;
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
