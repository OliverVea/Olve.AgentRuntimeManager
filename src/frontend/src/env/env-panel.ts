import { escapeHtml } from "../base-element.js";
import { checkEnvRows, type EnvRow, isBlankRow } from "./env-names.js";
import type { EnvRegistry } from "./env-registry.js";

const icon = (name: string) => `<svg class="i" aria-hidden="true"><use href="#i-${name}"/></svg>`;

type Fold = "own" | "registered" | "defaults";

/** The focused field, to put focus and caret back after a re-render. */
type Focus = { selector: string; start: number | null; end: number | null };

/**
 * The composer's env (approved mock: mocks/env.html): the `env` button in the settings row and
 * the panel under the row, in the page's light DOM so the page's styles apply. The panel holds
 * the session's own NAME = VALUE rows, the registered non-default variables to pick (`useEnv`)
 * and, folded, the default ones every agent gets.
 *
 * Typing never rebuilds the markup (that would lose focus and caret): rows are flagged in place,
 * and the panel is only re-rendered when rows come or go or the registered list changes.
 */
export class EnvPanel {
  /** The session's own rows, as typed. */
  rows: EnvRow[] = [];
  /** The registered, non-default variables picked for the session. */
  readonly picks = new Set<string>();
  #open = false;
  readonly #folds: Record<Fold, boolean> = { own: true, registered: true, defaults: false };

  constructor(
    readonly toggle: HTMLButtonElement,
    readonly panel: HTMLElement,
    readonly registry: EnvRegistry,
    readonly hooks: {
      /** "Manage in Options": open the Environment variables dialog. */
      manage: () => void;
      /** The rows or picks changed (the composer re-checks its Start button). */
      change: () => void;
    },
  ) {
    toggle.addEventListener("click", () => this.setOpen(!this.#open));
    registry.addEventListener("change", () => this.#registryChanged());
    panel.addEventListener("input", (e) => this.#input(e));
    panel.addEventListener("change", (e) => this.#pick(e));
    panel.addEventListener("click", (e) => this.#click(e));
    panel.addEventListener("keydown", (e) => this.#keydown(e));
    this.render();
  }

  get open(): boolean {
    return this.#open;
  }

  /** The picks, in the registered list's order. */
  get useEnv(): string[] {
    return this.registry.optional.map((v) => v.name).filter((name) => this.picks.has(name));
  }

  /** Whether a row has a problem (the session can't start until it's fixed). */
  get invalid(): boolean {
    return checkEnvRows(this.rows).env === undefined;
  }

  /** Opens or folds the panel; opening fetches the registered list. */
  setOpen(open: boolean): void {
    this.#open = open;
    this.panel.hidden = !open;
    if (open) void this.registry.load();
    this.render();
  }

  /** After a session is created: no rows, no picks. */
  clear(): void {
    this.rows = [];
    this.picks.clear();
    this.render();
    this.hooks.change();
  }

  /** Rebuilds the panel (structural changes), keeping the focused field and its caret. */
  render(): void {
    const focus = this.#focused();
    this.#saveFolds();
    const { optional, defaults } = this.registry;
    const count = this.rows.filter((r) => !isBlankRow(r)).length;
    const fold = (key: Fold, cls: string, summary: string, n: string, body: string) =>
      `<details class="fold${cls}" data-fold="${key}"${this.#folds[key] ? " open" : ""}><summary>${summary} <span class="n">${escapeHtml(n)}</span></summary>${body}</details>`;

    const rows = this.rows
      .map(
        (row, i) =>
          `<div class="env-row" data-i="${i}">` +
          `<input data-part="name" value="${escapeHtml(row.name)}" aria-label="name" placeholder="NAME" spellcheck="false" autocomplete="off">` +
          `<span class="eq">=</span>` +
          `<input data-part="value" value="${escapeHtml(row.value)}" aria-label="value" placeholder="value" spellcheck="false" autocomplete="off">` +
          `<button type="button" class="x-btn" data-remove title="Remove" aria-label="Remove">${icon("x")}</button>` +
          `<span class="err" hidden></span></div>`,
      )
      .join("");
    const own = fold(
      "own",
      "",
      "This session's own",
      String(count),
      `<div class="env-rows">${rows}</div>` +
        `<button type="button" class="link-btn" data-add>+ Add a variable</button>` +
        `<p class="not-secret">⚠ Not secret: shown with the session and in its events. Don't put tokens here.</p>`,
    );

    const chips = optional
      .map((v, i) => {
        const on = this.picks.has(v.name);
        return `<label class="chip${on ? " on" : ""}" title="${escapeHtml(v.value)}"><input type="checkbox" data-pick="${i}"${on ? " checked" : ""}> ${escapeHtml(v.name)}</label>`;
      })
      .join("");
    const registered = optional.length
      ? fold(
          "registered",
          "",
          "Registered, for this session",
          `${this.useEnv.length} of ${optional.length}`,
          `<div class="chips">${chips}</div>`,
        )
      : "";

    const manage = `<button type="button" class="link-btn" data-manage>Manage in Options</button>`;
    const everyAgent = defaults.length
      ? fold(
          "defaults",
          " defaults-fold",
          "Every agent also gets",
          String(defaults.length),
          `<p class="defaults">${defaults.map((v) => `<code title="${escapeHtml(v.value)}">${escapeHtml(v.name)}</code>`).join(", ")} · ${manage}</p>`,
        )
      : this.registry.loaded && !optional.length
        ? `<p class="defaults">No variables are registered on the server. · ${manage}</p>`
        : "";
    const problem = this.registry.problem
      ? `<p class="err">Couldn't load the registered variables: ${escapeHtml(this.registry.problem)}</p>`
      : "";

    this.panel.innerHTML = own + registered + everyAgent + problem;
    this.refresh();
    this.#restore(focus);
  }

  /** Updates what typing changes, in place: the rows' flags, the counts. */
  refresh(): void {
    const { problems } = checkEnvRows(this.rows);
    for (const row of this.panel.querySelectorAll<HTMLElement>(".env-row")) {
      const problem = problems[Number(row.dataset.i)];
      row.querySelector('[data-part="name"]')?.classList.toggle("bad", !!problem);
      const err = row.querySelector<HTMLElement>(".err");
      if (err) {
        err.textContent = problem ?? "";
        err.hidden = !problem;
      }
    }
    const count = this.rows.filter((r) => !isBlankRow(r)).length;
    const own = this.panel.querySelector('[data-fold="own"] .n');
    if (own) own.textContent = String(count);
    const registered = this.panel.querySelector('[data-fold="registered"] .n');
    if (registered)
      registered.textContent = `${this.useEnv.length} of ${this.registry.optional.length}`;

    const total = count + this.useEnv.length;
    this.toggle.setAttribute("aria-expanded", String(this.#open));
    this.toggle.title = this.#open ? "Hide this session's env" : "Show this session's env";
    this.toggle.innerHTML = `env${total ? ` <span class="count">${total}</span>` : ""} <span class="caret">${this.#open ? "▾" : "▸"}</span>`;
  }

  #registryChanged(): void {
    // A picked variable that's gone, or became default (every agent gets it now), isn't picked.
    const optional = new Set(this.registry.optional.map((v) => v.name));
    let dropped = false;
    for (const name of [...this.picks]) {
      if (!optional.has(name)) {
        this.picks.delete(name);
        dropped = true;
      }
    }
    this.render();
    if (dropped) this.hooks.change();
  }

  #input(e: Event): void {
    const input = e.target as HTMLInputElement;
    const i = Number(input.closest<HTMLElement>(".env-row")?.dataset.i);
    const row = this.rows[i];
    if (!row) return;
    if (input.dataset.part === "name") row.name = input.value;
    else if (input.dataset.part === "value") row.value = input.value;
    this.refresh();
    this.hooks.change();
  }

  #pick(e: Event): void {
    const box = e.target as HTMLInputElement;
    const variable = this.registry.optional[Number(box.dataset.pick)];
    if (box.dataset.pick === undefined || !variable) return;
    if (box.checked) this.picks.add(variable.name);
    else this.picks.delete(variable.name);
    box.closest(".chip")?.classList.toggle("on", box.checked);
    this.refresh();
    this.hooks.change();
  }

  #click(e: Event): void {
    const target = e.target as HTMLElement;
    if (target.closest("[data-add]")) {
      this.#folds.own = true;
      this.rows.push({ name: "", value: "" });
      this.render();
      this.panel
        .querySelector<HTMLInputElement>(`[data-i="${this.rows.length - 1}"] [data-part="name"]`)
        ?.focus();
      this.hooks.change();
    } else if (target.closest("[data-remove]")) {
      const i = Number(target.closest<HTMLElement>(".env-row")?.dataset.i);
      this.rows.splice(i, 1);
      this.render();
      this.panel.querySelector<HTMLElement>("[data-add]")?.focus();
      this.hooks.change();
    } else if (target.closest("[data-manage]")) {
      this.hooks.manage();
    }
  }

  /** Enter in an env field doesn't start the session (the form's implicit submit); Ctrl+Enter does. */
  #keydown(e: KeyboardEvent): void {
    if (e.key !== "Enter" || !(e.target instanceof HTMLInputElement)) return;
    e.preventDefault();
    if (e.ctrlKey || e.metaKey) this.panel.closest("form")?.requestSubmit();
  }

  #saveFolds(): void {
    for (const details of this.panel.querySelectorAll<HTMLDetailsElement>("details[data-fold]")) {
      this.#folds[details.dataset.fold as Fold] = details.open;
    }
  }

  #focused(): Focus | undefined {
    const active = document.activeElement;
    if (!(active instanceof HTMLInputElement) || !this.panel.contains(active)) return undefined;
    const row = active.closest<HTMLElement>(".env-row");
    const selector = row
      ? `[data-i="${row.dataset.i}"] [data-part="${active.dataset.part}"]`
      : `[data-pick="${active.dataset.pick}"]`;
    const text = active.type === "text";
    return {
      selector,
      start: text ? active.selectionStart : null,
      end: text ? active.selectionEnd : null,
    };
  }

  #restore(focus: Focus | undefined): void {
    if (!focus) return;
    const input = this.panel.querySelector<HTMLInputElement>(focus.selector);
    input?.focus();
    if (input && focus.start !== null) input.setSelectionRange(focus.start, focus.end);
  }
}
