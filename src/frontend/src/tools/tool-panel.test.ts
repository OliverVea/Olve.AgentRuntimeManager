import { afterEach, describe, expect, it, vi } from "vitest";
import { defaultToolList } from "../testing/fake-api.js";
import { ToolPanel } from "./tool-panel.js";

afterEach(() => {
  document.body.innerHTML = "";
});

function mount(tools = defaultToolList) {
  document.body.innerHTML = `<form id="composer"><button type="button" id="toggle"></button><div id="panel" hidden></div></form>`;
  const form = document.querySelector<HTMLFormElement>("#composer")!;
  const toggle = document.querySelector<HTMLButtonElement>("#toggle")!;
  const el = document.querySelector<HTMLElement>("#panel")!;
  const panel = new ToolPanel(toggle, el);
  panel.setTools(tools);
  return { form, toggle, el, panel };
}

const box = (el: HTMLElement, name: string) =>
  el.querySelector<HTMLInputElement>(`[data-pick="${name}"]`)!;
const ticked = (el: HTMLElement) =>
  [...el.querySelectorAll<HTMLInputElement>("[data-pick]")]
    .filter((b) => b.checked)
    .map((b) => b.dataset.pick);

function tick(el: HTMLElement, name: string, checked: boolean) {
  const b = box(el, name);
  b.checked = checked;
  b.dispatchEvent(new Event("change", { bubbles: true }));
}

const DEFAULTS = ["arm_bash", "arm_file_read", "arm_file_edit", "arm_file_create"];

describe("the composer's tools panel", () => {
  it("opens and folds from its button; a checkbox row per tool, its name and description", () => {
    const { el, toggle } = mount();
    expect(el.hidden).toBe(true);
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(toggle.title).toBe("Show this session's tools");
    expect(toggle.textContent).toBe("tools 4 ▸");

    toggle.click();
    expect(el.hidden).toBe(false);
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(toggle.title).toBe("Hide this session's tools");
    expect(toggle.textContent).toBe("tools 4 ▾");
    expect(el.querySelector("details, summary")).toBeNull(); // no fold of its own
    const rows = [...el.querySelectorAll(".tool-pick")];
    expect(
      rows.map((r) => [
        r.querySelector(".name")?.textContent,
        r.querySelector(".desc")?.textContent,
      ]),
    ).toEqual(defaultToolList.map((t) => [t.name, t.description]));

    toggle.click();
    expect(el.hidden).toBe(true);
    expect(toggle.textContent).toBe("tools 4 ▸"); // the count stays
  });

  it("starts with the server's defaults ticked: no tools field", () => {
    const { el, panel } = mount();
    expect(ticked(el)).toEqual(DEFAULTS);
    expect(el.querySelectorAll(".tool-pick.on")).toHaveLength(4);
    expect(panel.field).toBeUndefined();
  });

  it("counts what's picked, and shows no count when none is", () => {
    const { el, toggle, panel } = mount();
    tick(el, "arm_file_delete", true);
    expect(toggle.textContent).toBe("tools 5 ▸");
    expect(box(el, "arm_file_delete").closest(".tool-pick")?.classList.contains("on")).toBe(true);
    expect(panel.field).toEqual([...DEFAULTS, "arm_file_delete"]);

    for (const name of [...DEFAULTS, "arm_file_delete"]) tick(el, name, false);
    expect(toggle.textContent).toBe("tools ▸");
    expect(toggle.querySelector(".count")).toBeNull();
    expect(panel.field).toEqual([]);
  });

  it("is the defaults again after a create", () => {
    const { el, panel, toggle } = mount();
    tick(el, "arm_bash", false);
    expect(panel.field).toEqual(["arm_file_read", "arm_file_edit", "arm_file_create"]);
    panel.clear();
    expect(ticked(el)).toEqual(DEFAULTS);
    expect(toggle.textContent).toBe("tools 4 ▸");
    expect(panel.field).toBeUndefined();
  });

  it("sends no field while there's no list; a fresh list keeps a changed pick's tools that it still has", () => {
    const { el, panel, toggle } = mount();
    panel.setTools(undefined);
    expect(el.querySelectorAll(".tool-pick")).toHaveLength(0);
    expect(toggle.textContent).toBe("tools ▸");
    expect(panel.field).toBeUndefined();

    panel.setTools(defaultToolList);
    tick(el, "arm_file_delete", true);
    tick(el, "arm_bash", false);
    panel.setTools(defaultToolList.filter((t) => t.name !== "arm_file_delete"));
    expect(ticked(el)).toEqual(["arm_file_read", "arm_file_edit", "arm_file_create"]);

    // Signed out and in again: the defaults.
    panel.setTools(undefined);
    panel.setTools(defaultToolList);
    expect(ticked(el)).toEqual(DEFAULTS);
  });

  it("Enter on a checkbox doesn't start the session; Ctrl+Enter does", () => {
    const { el, form } = mount();
    const submit = vi.fn((e: Event) => e.preventDefault());
    form.addEventListener("submit", submit);
    const key = (init: KeyboardEventInit) => {
      const e = new KeyboardEvent("keydown", {
        key: "Enter",
        bubbles: true,
        cancelable: true,
        ...init,
      });
      box(el, "arm_bash").dispatchEvent(e);
      return e;
    };
    expect(key({}).defaultPrevented).toBe(true);
    expect(submit).not.toHaveBeenCalled();
    key({ ctrlKey: true });
    expect(submit).toHaveBeenCalledTimes(1);
  });
});
