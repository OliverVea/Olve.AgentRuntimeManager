import { afterEach, describe, expect, it, vi } from "vitest";
import { defaultSkillList } from "../testing/fake-api.js";
import { SkillPanel } from "./skill-panel.js";

afterEach(() => {
  document.body.innerHTML = "";
});

function mount(skills = defaultSkillList) {
  document.body.innerHTML = `<form id="composer"><button type="button" id="toggle"></button><div id="panel" hidden></div></form>`;
  const form = document.querySelector<HTMLFormElement>("#composer")!;
  const toggle = document.querySelector<HTMLButtonElement>("#toggle")!;
  const el = document.querySelector<HTMLElement>("#panel")!;
  const panel = new SkillPanel(toggle, el);
  panel.setSkills(skills);
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

const DEFAULTS = ["arm-conventions", "pipelines"];

describe("the composer's skills panel", () => {
  it("opens and folds from its button; a checkbox row per registered skill, its name and description", () => {
    const { el, toggle } = mount();
    expect(el.hidden).toBe(true);
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(toggle.title).toBe("Show this session's skills");
    expect(toggle.textContent).toBe("skills 2 ▸");

    toggle.click();
    expect(el.hidden).toBe(false);
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(toggle.title).toBe("Hide this session's skills");
    expect(toggle.textContent).toBe("skills 2 ▾");
    expect(el.querySelector("details, summary")).toBeNull(); // no fold of its own, as the tools panel
    const rows = [...el.querySelectorAll(".tool-pick")]; // the tools panel's rows and styles
    expect(
      rows.map((r) => [
        r.querySelector(".name")?.textContent,
        r.querySelector(".desc")?.textContent,
      ]),
    ).toEqual(defaultSkillList.map((s) => [s.name, s.description]));
    expect(el.querySelector(".defaults")).toBeNull();

    toggle.click();
    expect(el.hidden).toBe(true);
    expect(toggle.textContent).toBe("skills 2 ▸"); // the count stays
  });

  it("starts with the registered defaults ticked: no skills field", () => {
    const { el, panel } = mount();
    expect(ticked(el)).toEqual(DEFAULTS);
    expect(el.querySelectorAll(".tool-pick.on")).toHaveLength(2);
    expect(panel.field).toBeUndefined();
  });

  it("counts what's picked, and shows no count when none is", () => {
    const { el, toggle, panel } = mount();
    tick(el, "pdf", true);
    expect(toggle.textContent).toBe("skills 3 ▸");
    expect(box(el, "pdf").closest(".tool-pick")?.classList.contains("on")).toBe(true);
    expect(panel.field).toEqual(["arm-conventions", "pdf", "pipelines"]);

    for (const name of [...DEFAULTS, "pdf"]) tick(el, name, false);
    expect(toggle.textContent).toBe("skills ▸");
    expect(toggle.querySelector(".count")).toBeNull();
    expect(panel.field).toEqual([]);
  });

  it("says in one line when no skill is registered", () => {
    const { el, toggle, panel } = mount([]);
    expect(el.querySelectorAll(".tool-pick")).toHaveLength(0);
    expect(el.querySelector(".tool-rows > .defaults.none")?.textContent).toBe(
      "No skills registered.",
    );
    expect(el.querySelector(".tool-rows")?.children).toHaveLength(1);
    expect(toggle.textContent).toBe("skills ▸");
    expect(panel.field).toBeUndefined(); // nothing to pick from: the server's (empty) defaults

    panel.setSkills(defaultSkillList);
    expect(el.querySelector(".defaults")).toBeNull();
  });

  it("is the defaults again after a create", () => {
    const { el, panel, toggle } = mount();
    tick(el, "pipelines", false);
    expect(panel.field).toEqual(["arm-conventions"]);
    panel.clear();
    expect(ticked(el)).toEqual(DEFAULTS);
    expect(toggle.textContent).toBe("skills 2 ▸");
    expect(panel.field).toBeUndefined();
  });

  it("sends no field while there's no list; a fresh list keeps a changed pick's skills that it still has", () => {
    const { el, panel, toggle } = mount();
    panel.setSkills(undefined);
    expect(el.querySelectorAll(".tool-pick")).toHaveLength(0);
    expect(el.querySelector(".defaults")).toBeNull(); // no list isn't "none registered"
    expect(toggle.textContent).toBe("skills ▸");
    expect(panel.field).toBeUndefined();

    panel.setSkills(defaultSkillList);
    tick(el, "pdf", true);
    tick(el, "pipelines", false);
    panel.setSkills(defaultSkillList.filter((s) => s.name !== "pdf"));
    expect(ticked(el)).toEqual(["arm-conventions"]);

    // An untouched pick follows a changed default flag.
    panel.setSkills(undefined);
    panel.setSkills(defaultSkillList.map((s) => ({ ...s, default: s.name === "pdf" })));
    expect(ticked(el)).toEqual(["pdf"]);
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
      box(el, "pdf").dispatchEvent(e);
      return e;
    };
    expect(key({}).defaultPrevented).toBe(true);
    expect(submit).not.toHaveBeenCalled();
    key({ ctrlKey: true });
    expect(submit).toHaveBeenCalledTimes(1);
  });
});
