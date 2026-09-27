import type { EnvVariable } from "@arm/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../testing/fake-api.js";
import { EnvPanel } from "./env-panel.js";
import { EnvRegistry } from "./env-registry.js";

const variable = (name: string, value: string, isDefault = false): EnvVariable => ({
  name,
  value,
  default: isDefault,
  updatedAt: "2026-09-27T10:00:00Z",
});

const registered = () => [
  variable("GIT_AUTHOR_NAME", "Oliver Vea", true),
  variable("EXTRA_TOOLS", "ripgrep jq"),
  variable("GIT_SSH_COMMAND", "ssh -i key", true),
  variable("A_TOOL", 'say "hi" <b>'),
];

afterEach(() => {
  document.body.innerHTML = "";
});

async function mount(api = fakeApi({ env: registered() })) {
  document.body.innerHTML = `<form id="composer"><button type="button" id="toggle"></button><div id="panel" hidden></div></form>`;
  const registry = new EnvRegistry(api.client);
  const hooks = { manage: vi.fn(), change: vi.fn() };
  const form = document.querySelector<HTMLFormElement>("#composer")!;
  const toggle = document.querySelector<HTMLButtonElement>("#toggle")!;
  const el = document.querySelector<HTMLElement>("#panel")!;
  const panel = new EnvPanel(toggle, el, registry, hooks);
  return { api, registry, hooks, form, toggle, el, panel };
}

async function opened(api?: ReturnType<typeof fakeApi>) {
  const m = await mount(api);
  m.toggle.click();
  await vi.waitFor(() => expect(m.registry.loaded || m.registry.problem).toBeTruthy());
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

function addRow(el: HTMLElement, name: string, value: string) {
  q(el, "[data-add]").click();
  const rows = el.querySelectorAll(".env-row");
  const row = rows[rows.length - 1]!;
  type(q<HTMLInputElement>(row, '[data-part="name"]'), name);
  type(q<HTMLInputElement>(row, '[data-part="value"]'), value);
  return row;
}

describe("the composer's env panel", () => {
  it("opens from its button, fetching the registered list: picks for the non-default, the defaults folded", async () => {
    const { el, toggle, api } = await mount();
    expect(el.hidden).toBe(true);
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(toggle.textContent).toBe("env ▸");

    toggle.click();
    await vi.waitFor(() => expect(el.querySelectorAll(".chip")).toHaveLength(2));
    expect(api.envCalls().map((c) => c.method)).toEqual(["GET"]);
    expect(el.hidden).toBe(false);
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(toggle.title).toBe("Hide this session's env");
    expect(texts(el, "summary")).toEqual([
      "This session's own 0",
      "Registered, for this session 0 of 2",
      "Every agent also gets 2",
    ]);
    expect(texts(el, ".chip")).toEqual(["A_TOOL", "EXTRA_TOOLS"]);
    expect(texts(el, ".defaults code")).toEqual(["GIT_AUTHOR_NAME", "GIT_SSH_COMMAND"]);
    expect(q<HTMLDetailsElement>(el, ".defaults-fold").open).toBe(false);
    expect(q(el, ".not-secret").textContent).toContain("Not secret");

    toggle.click();
    expect(el.hidden).toBe(true);
    expect(toggle.textContent).toBe("env ▸");
  });

  it("escapes names and values, attributes included", async () => {
    const { el } = await opened();
    const chip = q(el, ".chip");
    expect(chip.title).toBe('say "hi" <b>');
    expect(el.querySelector("b")).toBeNull();

    addRow(el, "X", '"><img src=x>');
    q(el, "[data-add]").click(); // a re-render
    expect(el.querySelector("img")).toBeNull();
    expect(q<HTMLInputElement>(el, '[data-part="value"]').value).toBe('"><img src=x>');
  });

  it("flags a bad name on its row as it's typed, without rebuilding the row", async () => {
    const { el, panel, hooks, toggle } = await opened();
    q(el, "[data-add]").click();
    const name = q<HTMLInputElement>(el, '[data-part="name"]');
    expect(document.activeElement).toBe(name);

    type(name, "CLAUDE_CONFIG_DIR");
    expect(q(el, '[data-part="name"]')).toBe(name); // the same element: focus and caret stay
    expect(name.classList.contains("bad")).toBe(true);
    expect(q(el, ".env-row .err").textContent).toBe(
      "CLAUDE_CONFIG_DIR is set by ARM for its agents and can't be given.",
    );
    expect(panel.invalid).toBe(true);
    expect(hooks.change).toHaveBeenCalled();
    expect(toggle.textContent).toBe("env 1 ▾");

    type(name, "2-FAST");
    expect(q(el, ".env-row .err").textContent).toBe(
      "Names are letters, digits and _, not starting with a digit.",
    );
    type(name, "TASK_BRANCH");
    expect(name.classList.contains("bad")).toBe(false);
    expect(q(el, ".env-row .err").hidden).toBe(true);
    expect(panel.invalid).toBe(false);
  });

  it("flags a name given twice and a value without a name; ignores blank rows; removes rows", async () => {
    const { el, panel } = await opened();
    addRow(el, "A", "1");
    addRow(el, "A", "2");
    addRow(el, "", "orphan");
    addRow(el, "", "");
    expect(texts(el, ".env-row .err")).toEqual([
      "",
      "A is given twice.",
      "Give this value a name.",
      "",
    ]);
    expect(q(el, '[data-fold="own"] .n').textContent).toBe("3");

    q(el.querySelectorAll(".env-row")[2]!, "[data-remove]").click();
    q(el.querySelectorAll(".env-row")[1]!, "[data-remove]").click();
    expect(panel.rows).toEqual([
      { name: "A", value: "1" },
      { name: "", value: "" },
    ]);
    expect(panel.invalid).toBe(false);
    expect(q(el, '[data-fold="own"] .n').textContent).toBe("1");
  });

  it("picks registered variables for the session, dropping picks that are gone or became default", async () => {
    const { el, panel, registry, api, hooks, toggle } = await opened();
    for (const box of el.querySelectorAll<HTMLInputElement>(".chip input")) {
      box.click();
    }
    expect(panel.useEnv).toEqual(["A_TOOL", "EXTRA_TOOLS"]);
    expect(texts(el, ".chip.on")).toEqual(["A_TOOL", "EXTRA_TOOLS"]);
    expect(q(el, '[data-fold="registered"] .n').textContent).toBe("2 of 2");
    expect(toggle.textContent).toBe("env 2 ▾");

    api.env.splice(
      api.env.findIndex((v) => v.name === "A_TOOL"),
      1,
    );
    api.env.find((v) => v.name === "EXTRA_TOOLS")!.default = true;
    hooks.change.mockClear();
    await registry.load();
    expect(panel.useEnv).toEqual([]);
    expect([...panel.picks]).toEqual([]);
    expect(hooks.change).toHaveBeenCalled();
    expect(el.querySelector('[data-fold="registered"]')).toBeNull();
    expect(texts(el, ".defaults code")).toContain("EXTRA_TOOLS");
  });

  it("keeps a row's focus and caret when the registered list arrives while typing", async () => {
    const { el, registry } = await opened();
    const row = addRow(el, "TASK", "abc");
    const value = q<HTMLInputElement>(row, '[data-part="value"]');
    value.focus();
    value.setSelectionRange(1, 1);
    await registry.load();
    const now = q<HTMLInputElement>(el, '[data-part="value"]');
    expect(document.activeElement).toBe(now);
    expect(now.value).toBe("abc");
    expect(now.selectionStart).toBe(1);
  });

  it("doesn't start the session on Enter in its fields; Ctrl+Enter does", async () => {
    const { el, form } = await opened();
    const submit = vi.fn((e: Event) => e.preventDefault());
    form.addEventListener("submit", submit);
    const row = addRow(el, "A", "1");
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    q(row, '[data-part="value"]').dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    expect(submit).not.toHaveBeenCalled();

    q(row, '[data-part="value"]').dispatchEvent(
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

  it("keeps the folds as they were across re-renders", async () => {
    const { el } = await opened();
    q<HTMLDetailsElement>(el, '[data-fold="registered"]').open = false;
    q<HTMLDetailsElement>(el, '[data-fold="defaults"]').open = true;
    q(el, "[data-add]").click();
    expect(q<HTMLDetailsElement>(el, '[data-fold="registered"]').open).toBe(false);
    expect(q<HTMLDetailsElement>(el, '[data-fold="defaults"]').open).toBe(true);
  });

  it("opens the dialog from 'Manage in Options'; says when nothing is registered", async () => {
    const { el, hooks } = await opened();
    q(el, "[data-manage]").click();
    expect(hooks.manage).toHaveBeenCalledTimes(1);

    const empty = await opened(fakeApi());
    expect(q(empty.el, "p.defaults").textContent).toContain("No variables are registered");
    q(empty.el, "[data-manage]").click();
    expect(empty.hooks.manage).toHaveBeenCalledTimes(1);
  });

  it("shows a failed fetch inline", async () => {
    const api = fakeApi({ env: registered() });
    api.failEnv(500, "INTERNAL", "The database is locked.");
    const { el } = await opened(api);
    expect(q(el, "#panel > .err").textContent).toBe(
      "Couldn't load the registered variables: The database is locked.",
    );
  });

  it("clears its rows and picks (after a session is created)", async () => {
    const { el, panel, toggle } = await opened();
    addRow(el, "A", "1");
    q<HTMLInputElement>(el, ".chip input").click();
    panel.clear();
    expect(panel.rows).toEqual([]);
    expect(panel.useEnv).toEqual([]);
    expect(el.querySelectorAll(".env-row")).toHaveLength(0);
    expect(el.querySelectorAll(".chip.on")).toHaveLength(0);
    expect(toggle.textContent).toBe("env ▾");
  });
});
