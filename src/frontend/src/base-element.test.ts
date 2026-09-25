import { beforeAll, describe, expect, it } from "vitest";
import { BaseElement, escapeHtml } from "./base-element.js";

/** A tiny concrete element that exposes the protected seam for testing. */
class TestElement extends BaseElement {
  afterRenderCount = 0;

  protected override styles(): string {
    return ".x { color: red; }";
  }

  protected override template(): string {
    return `<p class="x">${escapeHtml(this.getString("label", "none"))}</p>`;
  }

  protected override afterRender(): void {
    this.afterRenderCount++;
  }

  // expose protected members for assertions
  public paint(): void {
    this.render();
  }
  public readNumber(): number {
    return this.getNumber("n", 7);
  }
  public readBoolean(): boolean {
    return this.getBoolean("flag");
  }
}

beforeAll(() => customElements.define("test-element", TestElement));

function make(): TestElement {
  return document.createElement("test-element") as TestElement;
}

describe("BaseElement", () => {
  it("renders template + scoped styles into the shadow root, but only when render() is called", () => {
    const el = make();
    expect(el.shadowRoot).not.toBeNull();
    expect(el.shadowRoot!.innerHTML).toBe(""); // nothing rendered yet

    el.paint();
    expect(el.shadowRoot!.querySelector("p.x")?.textContent).toBe("none");
    expect(el.shadowRoot!.querySelector("style")?.textContent).toContain("color: red");
    expect(el.afterRenderCount).toBe(1);
  });

  it("does NOT auto-render when an attribute changes — the hard rule (DESIGN §2.1)", () => {
    const el = make();
    el.paint();
    expect(el.shadowRoot!.querySelector("p")?.textContent).toBe("none");

    // Changing an attribute must not repaint on its own.
    el.setAttribute("label", "changed");
    expect(el.shadowRoot!.querySelector("p")?.textContent).toBe("none");
    expect(el.afterRenderCount).toBe(1);

    // Only an explicit render() reflects the new state.
    el.paint();
    expect(el.shadowRoot!.querySelector("p")?.textContent).toBe("changed");
    expect(el.afterRenderCount).toBe(2);
  });

  it("typed attribute helpers parse and fall back", () => {
    const el = make();
    expect(el.readNumber()).toBe(7); // fallback when absent
    el.setAttribute("n", "42");
    expect(el.readNumber()).toBe(42);
    el.setAttribute("n", "not-a-number");
    expect(el.readNumber()).toBe(7); // fallback when unparseable

    expect(el.readBoolean()).toBe(false);
    el.setAttribute("flag", "");
    expect(el.readBoolean()).toBe(true);
  });
});

describe("escapeHtml", () => {
  it("escapes HTML metacharacters so user text can't break markup / inject", () => {
    expect(escapeHtml(`<img src=x onerror="alert('xss')">`)).toBe(
      "&lt;img src=x onerror=&quot;alert(&#39;xss&#39;)&quot;&gt;",
    );
  });

  it("leaves ordinary text untouched", () => {
    expect(escapeHtml("hello world")).toBe("hello world");
  });
});
