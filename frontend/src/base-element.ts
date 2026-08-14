/**
 * `BaseElement` — the shared seam for the template's vanilla Web Components.
 *
 * It provides *ergonomics only*: a shadow root, typed attribute helpers, and a
 * string-template `render()`. It deliberately does **not** schedule renders, diff, or
 * observe state.
 *
 * ## The hard rule: explicit render, no automatic re-rendering
 *
 * `render()` is **author-called**. Nothing in this base class calls it for you — not the
 * attribute setters, not `attributeChangedCallback`. The component decides *when* the DOM
 * is (re)built. This is the DESIGN §2.1 stance: no framework runtime, no implicit reactive
 * re-render inherited by every component.
 *
 * ## Litify-later (opt-in, per component)
 *
 * Because every component is a standalone custom element, a component that outgrows this
 * can `npm i lit` and switch *its own* base to `LitElement` for auto-rerender + `lit-html`
 * diffing, while the rest keep using `BaseElement`. Auto-rerender is always a reach-for-it
 * choice, never the baseline (DESIGN §2.3).
 */
export abstract class BaseElement extends HTMLElement {
  /** The component's shadow root. Subclasses render into it via {@link render}. */
  protected readonly root: ShadowRoot;

  constructor() {
    super();
    this.root = this.attachShadow({ mode: "open" });
  }

  /** Author-defined, component-scoped CSS. Return a plain CSS string (no `<style>` tag). */
  protected styles(): string {
    return "";
  }

  /** Author-defined markup. Return an HTML string rendered into the shadow root. */
  protected template(): string {
    return "";
  }

  /**
   * Hook run at the end of every {@link render}. Attach event listeners and grab node
   * references here — `render()` replaces the shadow DOM, so listeners must be re-wired.
   */
  protected afterRender(): void {}

  /**
   * Explicitly (re)build the shadow DOM from {@link styles} + {@link template}, then run
   * {@link afterRender}. **Never called automatically by `BaseElement`.** The author calls
   * it — typically once from `connectedCallback`, then again after each state change.
   */
  protected render(): void {
    const css = this.styles();
    this.root.innerHTML = (css ? `<style>${css}</style>` : "") + this.template();
    this.afterRender();
  }

  // --- typed attribute helpers -------------------------------------------------
  // Pure get/set. Setters intentionally do NOT trigger a render — reading or writing an
  // attribute never re-renders on its own; call render() yourself when you want a repaint.

  protected getString(name: string, fallback = ""): string {
    return this.getAttribute(name) ?? fallback;
  }

  protected getNumber(name: string, fallback = 0): number {
    const raw = this.getAttribute(name);
    if (raw === null) return fallback;
    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : fallback;
  }

  protected getBoolean(name: string): boolean {
    return this.hasAttribute(name);
  }

  protected setString(name: string, value: string): void {
    this.setAttribute(name, value);
  }

  protected setBoolean(name: string, value: boolean): void {
    if (value) this.setAttribute(name, "");
    else this.removeAttribute(name);
  }

  /** Typed `querySelector` scoped to this component's shadow root. */
  protected query<E extends Element = Element>(selector: string): E | null {
    return this.root.querySelector<E>(selector);
  }

  /** Typed `querySelectorAll` scoped to this component's shadow root. */
  protected queryAll<E extends Element = Element>(selector: string): E[] {
    return Array.from(this.root.querySelectorAll<E>(selector));
  }
}

/**
 * Escape a string for safe interpolation into an HTML template. Component templates are
 * built with string concatenation and `innerHTML`, so any user-supplied text (message
 * bodies, error messages) MUST pass through this to avoid breaking markup / XSS.
 */
export function escapeHtml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
