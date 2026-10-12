import { escapeHtml } from "../base-element.js";
import { checkRepoRows, isBlankRepoRow, type RepoRow } from "./repo-rows.js";

const icon = (name: string) => `<svg class="i" aria-hidden="true"><use href="#i-${name}"/></svg>`;

/**
 * The composer's repos (M5e): the `repos` button next to env in the settings row and the panel
 * under the row, in the page's light DOM so the page's styles apply (the env panel's). The panel
 * holds the session's rows, a clone URL and an optional ref each, to add and remove.
 *
 * Typing never rebuilds the markup (that would lose focus and caret): rows are flagged in place,
 * and the panel is only re-rendered when rows come or go.
 */
export class RepoPanel {
  /** The session's rows, as typed. */
  rows: RepoRow[] = [];
  #open = false;
  #ownOpen = true;

  constructor(
    readonly toggle: HTMLButtonElement,
    readonly panel: HTMLElement,
    readonly hooks: {
      /** The rows changed (the composer re-checks its Start button). */
      change: () => void;
    },
  ) {
    toggle.addEventListener("click", () => this.setOpen(!this.#open));
    panel.addEventListener("input", (e) => this.#input(e));
    panel.addEventListener("click", (e) => this.#click(e));
    panel.addEventListener("keydown", (e) => this.#keydown(e));
    this.render();
  }

  get open(): boolean {
    return this.#open;
  }

  /** Whether a row has a problem (the session can't start until it's fixed). */
  get invalid(): boolean {
    return checkRepoRows(this.rows).repos === undefined;
  }

  /** Opens or folds the panel. */
  setOpen(open: boolean): void {
    this.#open = open;
    this.panel.hidden = !open;
    this.refresh();
  }

  /** After a session is created: no rows. */
  clear(): void {
    this.rows = [];
    this.render();
    this.hooks.change();
  }

  /** Rebuilds the panel (rows came or went). */
  render(): void {
    const fold = this.panel.querySelector<HTMLDetailsElement>('details[data-fold="own"]');
    if (fold) this.#ownOpen = fold.open;
    const rows = this.rows
      .map(
        (row, i) =>
          `<div class="repo-row" data-i="${i}">` +
          `<input data-part="url" value="${escapeHtml(row.url)}" aria-label="clone URL" placeholder="clone URL" spellcheck="false" autocomplete="off">` +
          `<span class="hash">#</span>` +
          `<input data-part="ref" value="${escapeHtml(row.ref)}" aria-label="ref" title="A branch, tag or commit. Empty: the default branch." placeholder="default branch" spellcheck="false" autocomplete="off">` +
          `<button type="button" class="x-btn" data-remove title="Remove" aria-label="Remove">${icon("x")}</button>` +
          `<span class="err" hidden></span></div>`,
      )
      .join("");
    this.panel.innerHTML =
      `<details class="fold" data-fold="own"${this.#ownOpen ? " open" : ""}><summary>This session's repos <span class="n">${this.#count()}</span></summary>` +
      `<div class="env-rows">${rows}</div><button type="button" class="link-btn" data-add>+ Add a repo</button></details>`;
    this.refresh();
  }

  /** Updates what typing changes, in place: the rows' flags, the counts. */
  refresh(): void {
    const { problems } = checkRepoRows(this.rows);
    for (const row of this.panel.querySelectorAll<HTMLElement>(".repo-row")) {
      const problem = problems[Number(row.dataset.i)];
      row.querySelector('[data-part="url"]')?.classList.toggle("bad", problem?.part === "url");
      row.querySelector('[data-part="ref"]')?.classList.toggle("bad", problem?.part === "ref");
      const err = row.querySelector<HTMLElement>(".err");
      if (err) {
        err.textContent = problem?.text ?? "";
        err.hidden = !problem;
      }
    }
    const count = this.#count();
    const n = this.panel.querySelector('[data-fold="own"] .n');
    if (n) n.textContent = String(count);
    this.toggle.setAttribute("aria-expanded", String(this.#open));
    this.toggle.title = this.#open ? "Hide this session's repos" : "Show this session's repos";
    this.toggle.innerHTML = `repos${count ? ` <span class="count">${count}</span>` : ""} <span class="caret">${this.#open ? "▾" : "▸"}</span>`;
  }

  #count(): number {
    return this.rows.filter((r) => !isBlankRepoRow(r)).length;
  }

  #input(e: Event): void {
    const input = e.target as HTMLInputElement;
    const row = this.rows[Number(input.closest<HTMLElement>(".repo-row")?.dataset.i)];
    if (!row) return;
    if (input.dataset.part === "url") row.url = input.value;
    else if (input.dataset.part === "ref") row.ref = input.value;
    this.refresh();
    this.hooks.change();
  }

  #click(e: Event): void {
    const target = e.target as HTMLElement;
    if (target.closest("[data-add]")) {
      this.rows.push({ url: "", ref: "" });
      this.#ownOpen = true;
      const fold = this.panel.querySelector<HTMLDetailsElement>('details[data-fold="own"]');
      if (fold) fold.open = true;
      this.render();
      this.panel
        .querySelector<HTMLInputElement>(`[data-i="${this.rows.length - 1}"] [data-part="url"]`)
        ?.focus();
      this.hooks.change();
    } else if (target.closest("[data-remove]")) {
      const i = Number(target.closest<HTMLElement>(".repo-row")?.dataset.i);
      this.rows.splice(i, 1);
      this.render();
      this.panel.querySelector<HTMLElement>("[data-add]")?.focus();
      this.hooks.change();
    }
  }

  /** Enter in a repos field doesn't start the session (the form's implicit submit); Ctrl+Enter does. */
  #keydown(e: KeyboardEvent): void {
    if (e.key !== "Enter" || !(e.target instanceof HTMLInputElement)) return;
    e.preventDefault();
    if (e.ctrlKey || e.metaKey) this.panel.closest("form")?.requestSubmit();
  }
}
