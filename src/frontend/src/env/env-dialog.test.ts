import type { EnvVariable } from "@arm/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../testing/fake-api.js";
import { EnvDialog } from "./env-dialog.js";
import { EnvRegistry } from "./env-registry.js";

const now = Date.parse("2026-09-27T12:00:00Z");
const variable = (name: string, value: string, isDefault = false, minutesAgo = 2): EnvVariable => ({
  name,
  value,
  default: isDefault,
  updatedAt: new Date(now - minutesAgo * 60_000).toISOString(),
});

afterEach(() => {
  document.body.innerHTML = "";
});

async function open(api = fakeApi({ env: [] })) {
  document.body.innerHTML = `<dialog id="env-dialog"></dialog>`;
  const registry = new EnvRegistry(api.client);
  const dialog = document.querySelector<HTMLDialogElement>("#env-dialog")!;
  const envDialog = new EnvDialog(dialog, registry, () => now);
  envDialog.open();
  await vi.waitFor(() => expect(registry.loaded || registry.problem).toBeTruthy());
  return { api, registry, dialog, envDialog };
}

const q = <E extends HTMLElement = HTMLElement>(el: ParentNode, selector: string) =>
  el.querySelector<E>(selector)!;
const items = (dialog: HTMLElement) => [...dialog.querySelectorAll<HTMLElement>(".env-item")];
const texts = (el: ParentNode, selector: string) =>
  [...el.querySelectorAll(selector)].map((e) => e.textContent?.trim());

function type(input: HTMLInputElement, value: string) {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

const listed = () =>
  fakeApi({
    env: [
      variable("GIT_SSH_COMMAND", "ssh -i ~/.ssh/key", true, 60),
      variable("EXTRA_TOOLS", "ripgrep jq"),
      variable("GIT_AUTHOR_NAME", "Oliver Vea", true, 180),
    ],
  });

describe("the Environment variables dialog", () => {
  it("lists the registered variables by name: value, every agent, updated, edit and delete", async () => {
    const { dialog, api } = await open(listed());
    expect(dialog.open).toBe(true);
    expect(api.envCalls().map((c) => c.method)).toEqual(["GET"]);
    expect(texts(dialog, ".env-item .name")).toEqual([
      "EXTRA_TOOLS",
      "GIT_AUTHOR_NAME",
      "GIT_SSH_COMMAND",
    ]);
    expect(texts(dialog, ".env-item .value")).toEqual([
      "ripgrep jq",
      "Oliver Vea",
      "ssh -i ~/.ssh/key",
    ]);
    expect(
      [...dialog.querySelectorAll<HTMLInputElement>("[data-default]")].map((b) => b.checked),
    ).toEqual([false, true, true]);
    expect(texts(dialog, ".env-item .updated")).toEqual(["2m ago", "3h ago", "1h ago"]);
    expect(dialog.querySelectorAll("[data-edit]")).toHaveLength(3);
    expect(dialog.querySelectorAll("[data-delete]")).toHaveLength(3);
    expect(q<HTMLDetailsElement>(dialog, "[data-add-form]").open).toBe(false);
    for (const button of dialog.querySelectorAll("button")) expect(button.type).toBe("button");
  });

  it("says when there are none, with the Add form open", async () => {
    const { dialog } = await open();
    expect(q(dialog, ".env-empty").textContent).toBe(
      "None yet. Add one below, e.g. your git identity for agents that commit.",
    );
    expect(q<HTMLDetailsElement>(dialog, "[data-add-form]").open).toBe(true);
  });

  it("escapes names and values, attributes included", async () => {
    const { dialog } = await open(fakeApi({ env: [variable("X", '"><img src=x> <b>bold</b>')] }));
    expect(dialog.querySelector("[data-list] img, [data-list] b")).toBeNull();
    expect(q(dialog, ".env-item .value").title).toBe('"><img src=x> <b>bold</b>');
    q(dialog, "[data-edit]").click();
    expect(q<HTMLInputElement>(dialog, "[data-edit-value]").value).toBe(
      '"><img src=x> <b>bold</b>',
    );
  });

  it("saves the 'every agent' switch at once, then fetches the list", async () => {
    const { dialog, api } = await open(listed());
    q<HTMLInputElement>(items(dialog)[0]!, "[data-default]").click();
    await vi.waitFor(() => expect(api.envCalls()).toHaveLength(3));
    expect(api.envCalls().slice(1)).toMatchObject([
      { method: "PUT", path: "/api/env/EXTRA_TOOLS", body: { value: "ripgrep jq", default: true } },
      { method: "GET" },
    ]);
    expect(q<HTMLInputElement>(items(dialog)[0]!, "[data-default]").checked).toBe(true);
  });

  it("shows a failed change under the list and the switch as the server has it", async () => {
    const { dialog, api } = await open(listed());
    api.failEnv(400, "INVALID_REQUEST", "Nope.");
    q<HTMLInputElement>(items(dialog)[0]!, "[data-default]").click();
    await vi.waitFor(() => expect(q(dialog, "[data-problem]").textContent).toBe("Nope."));
    expect(q<HTMLInputElement>(items(dialog)[0]!, "[data-default]").checked).toBe(false);
  });

  it("edits a value in place: Save sends it, Cancel drops it, Enter saves", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[1]!, "[data-edit]").click();
    const row = items(dialog)[1]!;
    expect(row.classList.contains("editing")).toBe(true);
    expect(q<HTMLInputElement>(row, ".edit-fields input[disabled]").value).toBe("GIT_AUTHOR_NAME");
    const value = q<HTMLInputElement>(row, "[data-edit-value]");
    expect(document.activeElement).toBe(value);
    type(value, "O. Vea");
    expect(q(dialog, "[data-edit-value]")).toBe(value); // typing doesn't rebuild the row
    q<HTMLInputElement>(row, "[data-edit-default]").click();
    expect(api.envCalls()).toHaveLength(1); // the edit's switch waits for Save
    q(row, "[data-save]").click();
    await vi.waitFor(() => expect(texts(dialog, ".env-item .value")[1]).toBe("O. Vea"));
    expect(api.envCalls()[1]).toMatchObject({
      method: "PUT",
      path: "/api/env/GIT_AUTHOR_NAME",
      body: { value: "O. Vea", default: false },
    });
    expect(dialog.querySelector(".editing")).toBeNull();

    q(items(dialog)[0]!, "[data-edit]").click();
    type(q<HTMLInputElement>(dialog, "[data-edit-value]"), "dropped");
    q(dialog, "[data-cancel]").click();
    expect(texts(dialog, ".env-item .value")[0]).toBe("ripgrep jq");

    q(items(dialog)[0]!, "[data-edit]").click();
    const input = q<HTMLInputElement>(dialog, "[data-edit-value]");
    type(input, "fd");
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    input.dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    await vi.waitFor(() => expect(texts(dialog, ".env-item .value")[0]).toBe("fd"));
    expect(dialog.open).toBe(true);
  });

  it("asks in the row before deleting; Keep backs out", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[2]!, "[data-delete]").click();
    const row = items(dialog)[2]!;
    expect(row.classList.contains("confirming")).toBe(true);
    expect(q(row, ".confirm span").textContent).toBe(
      "Delete GIT_SSH_COMMAND? New sessions stop getting it; existing ones keep their value.",
    );
    q(row, "[data-keep]").click();
    expect(dialog.querySelector(".confirming")).toBeNull();

    q(items(dialog)[2]!, "[data-delete]").click();
    q(dialog, "[data-confirm-delete]").click();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(2));
    expect(api.envCalls().slice(1)).toMatchObject([
      { method: "DELETE", path: "/api/env/GIT_SSH_COMMAND" },
      { method: "GET" },
    ]);
    expect(dialog.querySelector(".confirming")).toBeNull();
  });

  it("checks the Add form's name inline and adds a variable", async () => {
    const { dialog, api } = await open();
    const name = q<HTMLInputElement>(dialog, '[data-add="name"]');
    const add = q<HTMLButtonElement>(dialog, "[data-add-submit]");
    const err = q(dialog, "[data-add-err]");
    expect(add.disabled).toBe(true);

    type(name, "ARM_TOKEN");
    expect(name.classList.contains("bad")).toBe(true);
    expect(err.textContent).toBe("ARM_TOKEN is set by ARM for its agents and can't be registered.");
    expect(add.disabled).toBe(true);
    type(name, "9LIVES");
    expect(err.textContent).toBe("Names are letters, digits and _, not starting with a digit.");

    type(name, "GIT_AUTHOR_NAME");
    expect(err.hidden).toBe(true);
    expect(add.disabled).toBe(false);
    type(q<HTMLInputElement>(dialog, '[data-add="value"]'), "Oliver Vea");
    q<HTMLInputElement>(dialog, '[data-add="default"]').click();
    add.click();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(1));
    expect(api.envCalls()[1]).toMatchObject({
      method: "PUT",
      path: "/api/env/GIT_AUTHOR_NAME",
      body: { value: "Oliver Vea", default: true },
    });
    await vi.waitFor(() => expect(name.value).toBe(""));
    expect(q<HTMLInputElement>(dialog, '[data-add="value"]').value).toBe("");
    expect(q<HTMLInputElement>(dialog, '[data-add="default"]').checked).toBe(false);
    expect(add.disabled).toBe(true);

    type(name, "GIT_AUTHOR_NAME");
    expect(err.textContent).toBe("GIT_AUTHOR_NAME is already registered: change it in the list.");
  });

  it("adds on Enter without closing the dialog, and shows the API's refusal on the form", async () => {
    const { dialog, api } = await open();
    const name = q<HTMLInputElement>(dialog, '[data-add="name"]');
    type(name, "EXTRA_TOOLS");
    api.failEnv(400, "INVALID_REQUEST", "The server says no.");
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    name.dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    await vi.waitFor(() =>
      expect(q(dialog, "[data-add-err]").textContent).toBe("The server says no."),
    );
    expect(name.classList.contains("bad")).toBe(true);
    expect(q<HTMLButtonElement>(dialog, "[data-add-submit]").disabled).toBe(false);
    expect(dialog.open).toBe(true);
    expect(name.value).toBe("EXTRA_TOOLS");

    const submit = new Event("submit", { cancelable: true });
    q(dialog, "form").dispatchEvent(submit);
    expect(submit.defaultPrevented).toBe(true);
  });

  it("shows a failed fetch", async () => {
    const api = fakeApi();
    api.failEnv(401, "UNAUTHORIZED", "");
    const { dialog } = await open(api);
    expect(q(dialog, "[data-problem]").textContent).toBe(
      "Couldn't load the variables: Not authorized — try logging in again.",
    );
  });
});
