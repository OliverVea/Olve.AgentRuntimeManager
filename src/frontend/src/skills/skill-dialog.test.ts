import type { Skill } from "@arm/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../testing/fake-api.js";
import { SkillDialog, sourceText } from "./skill-dialog.js";
import { SkillRegistry } from "./skill-registry.js";

const now = Date.parse("2026-10-27T12:00:00Z");
const skill = (
  name: string,
  description: string,
  source: Skill["source"],
  isDefault = false,
  minutesAgo = 2,
): Skill => ({
  name,
  description,
  default: isDefault,
  source,
  updatedAt: new Date(now - minutesAgo * 60_000).toISOString(),
});

afterEach(() => {
  document.body.innerHTML = "";
});

async function open(api = fakeApi({ skills: [] })) {
  document.body.innerHTML = `<dialog id="skills-dialog"></dialog>`;
  const registry = new SkillRegistry(api.client);
  const dialog = document.querySelector<HTMLDialogElement>("#skills-dialog")!;
  const skillDialog = new SkillDialog(dialog, registry, () => now);
  skillDialog.open();
  await vi.waitFor(() => expect(registry.loaded || registry.problem).toBeTruthy());
  return { api, registry, dialog, skillDialog };
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
    skills: [
      skill(
        "pipelines",
        "Working with Olve.Pipelines.",
        { url: "git@github.com:OliverVea/ovea-skills.git", ref: "main", path: "skills/pl" },
        true,
        180,
      ),
      skill("pdf", "PDF manipulation.", {
        url: "https://github.com/anthropics/skills",
        path: "skills/pdf",
      }),
      skill(
        "arm-conventions",
        "ARM's own conventions.",
        { url: "git@github.com:OliverVea/Olve.AgentRuntimeManager.git" },
        true,
        60,
      ),
    ],
  });

describe("the Skills dialog", () => {
  it("lists the registered skills by name: description, default, updated, source and delete", async () => {
    const { dialog, api } = await open(listed());
    expect(dialog.open).toBe(true);
    expect(dialog.classList.contains("env-dialog")).toBe(true);
    expect(dialog.classList.contains("skills-dialog")).toBe(true);
    expect(q(dialog, "h3").textContent).toBe("Skills");
    expect(q(dialog, "form > p").textContent).toBe(
      "What agents can load with arm_skill_load. Default ones go to every session that picks none. A session copies its skills when it starts.",
    );
    expect(api.skillCalls().map((c) => c.method)).toEqual(["GET"]);
    expect(texts(dialog, ".env-item .name")).toEqual(["arm-conventions", "pdf", "pipelines"]);
    expect(texts(dialog, ".env-item .value.desc")).toEqual([
      "ARM's own conventions.",
      "PDF manipulation.",
      "Working with Olve.Pipelines.",
    ]);
    expect(q(items(dialog)[1]!, ".desc").title).toBe("PDF manipulation.");
    expect(
      [...dialog.querySelectorAll<HTMLInputElement>("[data-default]")].map((b) => b.checked),
    ).toEqual([true, false, true]);
    expect(texts(dialog, ".env-item .switch")).toEqual(["default", "default", "default"]);
    expect(texts(dialog, ".env-item .updated")).toEqual(["1h ago", "2m ago", "3h ago"]);
    expect(texts(dialog, ".env-item .src")).toEqual([
      "git@github.com:OliverVea/Olve.AgentRuntimeManager.git",
      "https://github.com/anthropics/skills skills/pdf",
      "git@github.com:OliverVea/ovea-skills.git#main skills/pl",
    ]);
    expect(q(items(dialog)[2]!, ".src").title).toBe(
      "git@github.com:OliverVea/ovea-skills.git#main skills/pl",
    );
    expect(dialog.querySelectorAll("[data-delete]")).toHaveLength(3);
    expect(dialog.querySelector("[data-edit]")).toBeNull(); // not editable in place
    expect(q<HTMLDetailsElement>(dialog, "[data-install-form]").open).toBe(false);
    for (const button of dialog.querySelectorAll("button")) expect(button.type).toBe("button");
  });

  it("says when there are none, with the install form open", async () => {
    const { dialog } = await open();
    expect(q(dialog, ".env-empty").textContent).toBe("No skills registered.");
    expect(q<HTMLDetailsElement>(dialog, "[data-install-form]").open).toBe(true);
  });

  it("escapes names, descriptions and sources, attributes included", async () => {
    const { dialog } = await open(
      fakeApi({
        skills: [skill("x", '"><img src=x> <b>bold</b>', { url: "<i>u</i>", path: '"p' })],
      }),
    );
    expect(dialog.querySelector("[data-list] img, [data-list] b, [data-list] i")).toBeNull();
    expect(q(dialog, ".env-item .desc").title).toBe('"><img src=x> <b>bold</b>');
    expect(q(dialog, ".env-item .src").title).toBe('<i>u</i> "p');
  });

  it("saves the default switch at once, from the same source, then fetches the list", async () => {
    const { dialog, api } = await open(listed());
    q<HTMLInputElement>(items(dialog)[1]!, "[data-default]").click();
    await vi.waitFor(() => expect(api.skillCalls()).toHaveLength(3));
    expect(api.skillCalls().slice(1)).toMatchObject([
      {
        method: "PUT",
        path: "/api/skills/pdf",
        body: {
          source: { url: "https://github.com/anthropics/skills", path: "skills/pdf" },
          default: true,
        },
      },
      { method: "GET" },
    ]);
    expect(q<HTMLInputElement>(items(dialog)[1]!, "[data-default]").checked).toBe(true);
  });

  it("shows a failed change under the list and the switch as the server has it", async () => {
    const { dialog, api } = await open(listed());
    api.failSkills(400, "SKILL_SOURCE_FAILED", "Couldn't fetch it.");
    q<HTMLInputElement>(items(dialog)[1]!, "[data-default]").click();
    await vi.waitFor(() =>
      expect(q(dialog, "[data-problem]").textContent).toBe("Couldn't fetch it."),
    );
    expect(q<HTMLInputElement>(items(dialog)[1]!, "[data-default]").checked).toBe(false);
  });

  it("asks in the row before deleting; Keep backs out", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[1]!, "[data-delete]").click();
    const row = items(dialog)[1]!;
    expect(row.classList.contains("confirming")).toBe(true);
    expect(q(row, ".confirm span").textContent).toBe(
      "Delete pdf? New sessions stop getting it; running ones keep their copy.",
    );
    q(row, "[data-keep]").click();
    expect(dialog.querySelector(".confirming")).toBeNull();

    q(items(dialog)[1]!, "[data-delete]").click();
    q(dialog, "[data-confirm-delete]").click();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(2));
    expect(api.skillCalls().slice(1)).toMatchObject([
      { method: "DELETE", path: "/api/skills/pdf" },
      { method: "GET" },
    ]);
    expect(dialog.querySelector(".confirming")).toBeNull();
  });

  it("enables Install with a name and a repository, checking the name inline, and installs", async () => {
    const { dialog, api } = await open();
    const name = q<HTMLInputElement>(dialog, '[data-install="name"]');
    const url = q<HTMLInputElement>(dialog, '[data-install="url"]');
    const install = q<HTMLButtonElement>(dialog, "[data-install-submit]");
    const err = q(dialog, "[data-install-err]");
    expect(install.disabled).toBe(true);
    expect(name.placeholder).toBe("pdf");
    expect(url.placeholder).toBe("https://github.com/anthropics/skills");
    expect(q<HTMLInputElement>(dialog, '[data-install="ref"]').placeholder).toBe("default branch");
    expect(q<HTMLInputElement>(dialog, '[data-install="path"]').placeholder).toBe("skills/pdf");

    type(name, "pdf");
    expect(install.disabled).toBe(true); // no repository yet
    expect(err.hidden).toBe(true);
    type(url, "https://github.com/anthropics/skills");
    expect(install.disabled).toBe(false);

    type(name, "PDF");
    expect(name.classList.contains("bad")).toBe(true);
    expect(err.textContent).toBe("Names are lowercase letters, digits and -, not starting with -.");
    expect(install.disabled).toBe(true);
    type(name, "-pdf");
    expect(install.disabled).toBe(true);
    type(name, "pdf");
    expect(name.classList.contains("bad")).toBe(false);
    expect(err.hidden).toBe(true);
    expect(install.disabled).toBe(false);

    type(q<HTMLInputElement>(dialog, '[data-install="path"]'), " skills/pdf ");
    q<HTMLInputElement>(dialog, '[data-install="default"]').click();
    install.click();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(1));
    expect(api.skillCalls()[1]).toMatchObject({
      method: "PUT",
      path: "/api/skills/pdf",
      body: {
        source: { url: "https://github.com/anthropics/skills", path: "skills/pdf" },
        default: true,
      },
    });
    expect((api.skillCalls()[1]!.body as { source: object }).source).not.toHaveProperty("ref");
    await vi.waitFor(() => expect(name.value).toBe(""));
    expect(url.value).toBe("");
    expect(q<HTMLInputElement>(dialog, '[data-install="path"]').value).toBe("");
    expect(q<HTMLInputElement>(dialog, '[data-install="default"]').checked).toBe(false);
    expect(install.disabled).toBe(true);
    expect(document.activeElement).toBe(name);
  });

  it("installs a name that exists again (replacing it), with a ref", async () => {
    const { dialog, api } = await open(listed());
    const name = q<HTMLInputElement>(dialog, '[data-install="name"]');
    type(name, "pdf");
    type(q<HTMLInputElement>(dialog, '[data-install="url"]'), "https://github.com/x/skills");
    type(q<HTMLInputElement>(dialog, '[data-install="ref"]'), "v2");
    expect(q(dialog, "[data-install-err]").hidden).toBe(true);
    q<HTMLButtonElement>(dialog, "[data-install-submit]").click();
    await vi.waitFor(() => expect(api.skillCalls()).toHaveLength(3));
    expect(api.skillCalls()[1]).toMatchObject({
      method: "PUT",
      path: "/api/skills/pdf",
      body: { source: { url: "https://github.com/x/skills", ref: "v2" }, default: false },
    });
    expect(items(dialog)).toHaveLength(3);
    expect(texts(dialog, ".env-item .src")[1]).toBe("https://github.com/x/skills#v2");
  });

  it("installs on Enter without closing the dialog, and shows the server's refusal under the form", async () => {
    const { dialog, api } = await open();
    const name = q<HTMLInputElement>(dialog, '[data-install="name"]');
    const url = q<HTMLInputElement>(dialog, '[data-install="url"]');
    type(name, "xlsx");
    type(url, "https://github.com/anthropics/skillz");
    api.failSkills(
      400,
      "SKILL_SOURCE_FAILED",
      "Couldn't fetch https://github.com/anthropics/skillz: remote: Repository not found.",
    );
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    url.dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    await vi.waitFor(() =>
      expect(q(dialog, "[data-install-err]").textContent).toBe(
        "Couldn't fetch https://github.com/anthropics/skillz: remote: Repository not found.",
      ),
    );
    expect(url.classList.contains("bad")).toBe(true); // the repository couldn't be fetched
    expect(name.classList.contains("bad")).toBe(false);
    expect(q<HTMLButtonElement>(dialog, "[data-install-submit]").disabled).toBe(false);
    expect(dialog.open).toBe(true);
    expect(name.value).toBe("xlsx");
    expect(url.value).toBe("https://github.com/anthropics/skillz");

    type(url, "https://github.com/anthropics/skills"); // typing again clears it
    expect(q(dialog, "[data-install-err]").hidden).toBe(true);
    expect(url.classList.contains("bad")).toBe(false);

    // Another refusal marks no field: there's no SKILL.md to read at the path.
    api.failSkills(400, "INVALID_SKILL", "No SKILL.md at skills/xlsx.");
    q<HTMLButtonElement>(dialog, "[data-install-submit]").click();
    await vi.waitFor(() =>
      expect(q(dialog, "[data-install-err]").textContent).toBe("No SKILL.md at skills/xlsx."),
    );
    expect(dialog.querySelector(".fields .bad")).toBeNull();

    const submit = new Event("submit", { cancelable: true });
    q(dialog, "form").dispatchEvent(submit);
    expect(submit.defaultPrevented).toBe(true);
  });

  it("keeps Install disabled while one is in flight, and its refusal through a list refresh", async () => {
    const { dialog, api, registry } = await open(listed());
    const name = q<HTMLInputElement>(dialog, '[data-install="name"]');
    const install = q<HTMLButtonElement>(dialog, "[data-install-submit]");
    const err = q(dialog, "[data-install-err]");
    type(name, "new-one");
    type(q<HTMLInputElement>(dialog, '[data-install="url"]'), "https://github.com/x/y");
    api.holdSkillWrites();
    install.click();
    expect(install.disabled).toBe(true);
    await vi.waitFor(() => expect(api.skillCalls().some((c) => c.method === "PUT")).toBe(true));
    await registry.load(); // a refresh while the PUT is on its way
    expect(install.disabled).toBe(true);
    api.failSkills(400, "INVALID_SKILL", "The server says no.");
    api.releaseSkillWrites();
    await vi.waitFor(() => expect(err.textContent).toBe("The server says no."));
    expect(install.disabled).toBe(false);

    await registry.load(); // e.g. after a switch elsewhere in the list
    expect(err.textContent).toBe("The server says no.");

    type(name, "new-two"); // typing again clears it
    expect(err.hidden).toBe(true);
  });

  it("shows a failed fetch", async () => {
    const api = fakeApi();
    api.failSkills(401, "UNAUTHORIZED", "");
    const { dialog } = await open(api);
    expect(q(dialog, "[data-problem]").textContent).toBe(
      "Couldn't load the skills: Not authorized — try logging in again.",
    );
  });

  it("writes a source as url#ref path", () => {
    expect(sourceText({ url: "https://x/y" })).toBe("https://x/y");
    expect(sourceText({ url: "https://x/y", ref: "main" })).toBe("https://x/y#main");
    expect(sourceText({ url: "https://x/y", path: "skills/a" })).toBe("https://x/y skills/a");
    expect(sourceText({ url: "https://x/y", ref: "v1", path: "a" })).toBe("https://x/y#v1 a");
  });
});
