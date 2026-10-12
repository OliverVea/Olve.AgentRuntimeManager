import { afterEach, describe, expect, it, vi } from "vitest";
import { RepoPanel } from "./repo-panel.js";

afterEach(() => {
  document.body.innerHTML = "";
});

function mount() {
  document.body.innerHTML = `<form id="composer"><button type="button" id="toggle"></button><div id="panel" hidden></div></form>`;
  const hooks = { change: vi.fn() };
  const form = document.querySelector<HTMLFormElement>("#composer")!;
  const toggle = document.querySelector<HTMLButtonElement>("#toggle")!;
  const el = document.querySelector<HTMLElement>("#panel")!;
  const panel = new RepoPanel(toggle, el, hooks);
  return { hooks, form, toggle, el, panel };
}

function opened() {
  const m = mount();
  m.toggle.click();
  return m;
}

const q = <E extends Element = HTMLElement>(el: Element, selector: string) =>
  el.querySelector<E & HTMLElement>(selector)!;
const texts = (el: Element, selector: string) =>
  [...el.querySelectorAll(selector)].map((e) => e.textContent?.trim());

function type(input: HTMLInputElement, value: string) {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

function addRow(el: HTMLElement, url: string, ref = "") {
  q(el, "[data-add]").click();
  const rows = el.querySelectorAll(".repo-row");
  const row = rows[rows.length - 1]!;
  type(q<HTMLInputElement>(row, '[data-part="url"]'), url);
  type(q<HTMLInputElement>(row, '[data-part="ref"]'), ref);
  return row;
}

const ARM = "git@github.com:OliverVea/Olve.AgentRuntimeManager.git";
const TRAINS = "git@github.com:OliverVea/Olve.Trains.git";

describe("the composer's repos panel", () => {
  it("opens and folds from its button; one fold of the session's repos with an add link", () => {
    const { el, toggle } = mount();
    expect(el.hidden).toBe(true);
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(toggle.title).toBe("Show this session's repos");
    expect(toggle.textContent).toBe("repos ▸");

    toggle.click();
    expect(el.hidden).toBe(false);
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(toggle.title).toBe("Hide this session's repos");
    expect(toggle.textContent).toBe("repos ▾");
    expect(texts(el, "summary")).toEqual(["This session's repos 0"]);
    expect(q<HTMLDetailsElement>(el, '[data-fold="own"]').open).toBe(true);
    expect(q(el, "[data-add]").textContent).toBe("+ Add a repo");

    toggle.click();
    expect(el.hidden).toBe(true);
    expect(toggle.textContent).toBe("repos ▸");
  });

  it("adds a row focused on its URL: clone URL # ref (empty: the default branch), and counts the filled ones", () => {
    const { el, toggle, hooks, panel } = opened();
    q(el, "[data-add]").click();
    const url = q<HTMLInputElement>(el, '[data-part="url"]');
    const ref = q<HTMLInputElement>(el, '[data-part="ref"]');
    expect(document.activeElement).toBe(url);
    expect(url.placeholder).toBe("clone URL");
    expect(url.getAttribute("aria-label")).toBe("clone URL");
    expect(ref.placeholder).toBe("default branch");
    expect(ref.title).toBe("A branch, tag or commit. Empty: the default branch.");
    expect(q(el, ".repo-row .hash").textContent).toBe("#");
    expect(hooks.change).toHaveBeenCalled();
    expect(toggle.textContent).toBe("repos ▾"); // a blank row doesn't count

    type(url, ARM);
    addRow(el, TRAINS, "main");
    expect(q(el, '[data-fold="own"] .n').textContent).toBe("2");
    expect(toggle.textContent).toBe("repos 2 ▾");
    expect(panel.rows).toEqual([
      { url: ARM, ref: "" },
      { url: TRAINS, ref: "main" },
    ]);
    expect(panel.invalid).toBe(false);

    toggle.click(); // folded, the count stays
    expect(toggle.textContent).toBe("repos 2 ▸");
  });

  it("flags a bad row on its field as it's typed, without rebuilding the row", () => {
    const { el, panel, hooks } = opened();
    q(el, "[data-add]").click();
    const url = q<HTMLInputElement>(el, '[data-part="url"]');
    const ref = q<HTMLInputElement>(el, '[data-part="ref"]');

    type(url, "--upload-pack=evil");
    expect(q(el, '[data-part="url"]')).toBe(url); // the same element: focus and caret stay
    expect(url.classList.contains("bad")).toBe(true);
    expect(q(el, ".repo-row .err").textContent).toBe("A clone URL can't start with -.");
    expect(q(el, ".repo-row .err").hidden).toBe(false);
    expect(panel.invalid).toBe(true);
    expect(hooks.change).toHaveBeenCalled();

    type(url, ARM);
    type(ref, "my branch");
    expect(url.classList.contains("bad")).toBe(false);
    expect(ref.classList.contains("bad")).toBe(true);
    expect(q(el, ".repo-row .err").textContent).toBe(
      "A ref is a branch, tag or commit: no spaces, not starting with -.",
    );

    type(ref, "main");
    expect(ref.classList.contains("bad")).toBe(false);
    expect(q(el, ".repo-row .err").hidden).toBe(true);
    expect(panel.invalid).toBe(false);
  });

  it("flags a ref without a URL and a second repo in one folder; ignores blank rows; removes rows", () => {
    const { el, panel } = opened();
    addRow(el, TRAINS);
    addRow(el, "https://github.com/OliverVea/Olve.Trains", "4c1d9e7");
    addRow(el, "", "main");
    addRow(el, "", "");
    expect(texts(el, ".repo-row .err")).toEqual([
      "",
      "Another repo above would also go in the folder Olve.Trains.",
      "Give this ref a clone URL.",
      "",
    ]);
    expect(q(el, '[data-fold="own"] .n').textContent).toBe("3");

    q(el.querySelectorAll(".repo-row")[2]!, "[data-remove]").click();
    expect(document.activeElement).toBe(q(el, "[data-add]"));
    q(el.querySelectorAll(".repo-row")[1]!, "[data-remove]").click();
    expect(panel.rows).toEqual([
      { url: TRAINS, ref: "" },
      { url: "", ref: "" },
    ]);
    expect(panel.invalid).toBe(false);
    expect(q(el, '[data-fold="own"] .n').textContent).toBe("1");
  });

  it("escapes what's typed when it re-renders", () => {
    const { el } = opened();
    addRow(el, '"><img src=x>', '"><b>x</b>');
    q(el, "[data-add]").click(); // a re-render
    expect(el.querySelector("img")).toBeNull();
    expect(el.querySelector("b")).toBeNull();
    expect(q<HTMLInputElement>(el, '[data-part="url"]').value).toBe('"><img src=x>');
    expect(q<HTMLInputElement>(el, '[data-part="ref"]').value).toBe('"><b>x</b>');
  });

  it("keeps its fold as it was across re-renders, and opens it to add a row", () => {
    const { el } = opened();
    addRow(el, ARM);
    q<HTMLDetailsElement>(el, '[data-fold="own"]').open = false;
    q(el.querySelector(".repo-row")!, "[data-remove]").click();
    expect(q<HTMLDetailsElement>(el, '[data-fold="own"]').open).toBe(false);

    q(el, "[data-add]").click();
    expect(q<HTMLDetailsElement>(el, '[data-fold="own"]').open).toBe(true);
  });

  it("doesn't start the session on Enter in its fields; Ctrl+Enter does", () => {
    const { el, form } = opened();
    const submit = vi.fn((e: Event) => e.preventDefault());
    form.addEventListener("submit", submit);
    const row = addRow(el, ARM);
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    q(row, '[data-part="ref"]').dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    expect(submit).not.toHaveBeenCalled();

    q(row, '[data-part="url"]').dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "Enter",
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    );
    expect(submit).toHaveBeenCalledTimes(1);
    for (const button of el.querySelectorAll("button")) expect(button.type).toBe("button");
  });

  it("clears its rows (after a session is created)", () => {
    const { el, panel, toggle, hooks } = opened();
    addRow(el, ARM, "main");
    hooks.change.mockClear();
    panel.clear();
    expect(panel.rows).toEqual([]);
    expect(el.querySelectorAll(".repo-row")).toHaveLength(0);
    expect(toggle.textContent).toBe("repos ▾");
    expect(hooks.change).toHaveBeenCalled();
  });
});
