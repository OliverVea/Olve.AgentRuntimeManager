import type { McpServer } from "@arm/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../testing/fake-api.js";
import { ToolCatalog } from "../tools/tool-catalog.js";
import { McpDialog } from "./mcp-dialog.js";
import { McpRegistry } from "./mcp-registry.js";

const now = Date.parse("2026-11-03T12:00:00Z");
const server = (
  name: string,
  description: string,
  command: string,
  args: string[],
  env: Record<string, string> = {},
  minutesAgo = 2,
): McpServer => ({
  name,
  description,
  command,
  args,
  env,
  updatedAt: new Date(now - minutesAgo * 60_000).toISOString(),
});

afterEach(() => {
  document.body.innerHTML = "";
});

/** The dialog open, wired as main.ts does: every change fetches the tools list again. */
async function open(api = fakeApi()) {
  document.body.innerHTML = `<dialog id="mcp-dialog"></dialog>`;
  const registry = new McpRegistry(api.client);
  const tools = new ToolCatalog(api.client);
  registry.addEventListener("written", () => void tools.load());
  const dialog = document.querySelector<HTMLDialogElement>("#mcp-dialog")!;
  new McpDialog(dialog, registry, () => now).open();
  await vi.waitFor(() => expect(registry.loaded || registry.problem).toBeTruthy());
  return { api, registry, dialog };
}

const q = <E extends HTMLElement = HTMLElement>(el: ParentNode, selector: string) =>
  el.querySelector<E>(selector)!;
const items = (dialog: HTMLElement) => [...dialog.querySelectorAll<HTMLElement>(".env-item")];
const texts = (el: ParentNode, selector: string) =>
  [...el.querySelectorAll(selector)].map((e) => e.textContent?.trim());
const toolFetches = (api: ReturnType<typeof fakeApi>) =>
  api.calls.filter((c) => c.path === "/api/tools").length;

function type(field: HTMLInputElement | HTMLTextAreaElement, value: string) {
  field.value = value;
  field.dispatchEvent(new Event("input", { bubbles: true }));
}
const reg = (dialog: HTMLElement, field: string) =>
  q<HTMLInputElement | HTMLTextAreaElement>(dialog, `[data-register="${field}"]`);
const edit = (dialog: HTMLElement, field: string) =>
  q<HTMLInputElement | HTMLTextAreaElement>(dialog, `[data-edit="${field}"]`);
const enter = () => new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });

const github = server(
  "github",
  "GitHub's issues, pull requests and code.",
  "npx",
  ["-y", "@modelcontextprotocol/server-github@2025.4.8"],
  { GITHUB_PERSONAL_ACCESS_TOKEN: "github_pat_fake" },
  120,
);
const time = server("time", "The current time.", "uvx", [
  "mcp-server-time==2026.1.26",
  "--zone=a b",
]);
const listed = () => fakeApi({ mcpServers: [time, github] });

describe("the MCP servers dialog", () => {
  it("lists the servers by name: description, updated, edit and delete, the command line and env names", async () => {
    const { dialog, api } = await open(listed());
    expect(dialog.open).toBe(true);
    expect([...dialog.classList]).toEqual(["env-dialog", "mcp-dialog"]);
    expect(q(dialog, "h3").textContent).toBe("MCP servers");
    expect(q(dialog, "form > p").textContent).toBe(
      "Servers a session can name in its tools; its agent gets all of their tools. A session keeps the definition it was created with.",
    );
    expect(api.mcpCalls().map((c) => c.method)).toEqual(["GET"]);
    expect(texts(dialog, ".env-item .name")).toEqual(["github", "time"]);
    expect(texts(dialog, ".env-item .desc")).toEqual([
      "GitHub's issues, pull requests and code.",
      "The current time.",
    ]);
    expect(texts(dialog, ".env-item .updated")).toEqual(["2h ago", "2m ago"]);
    expect(texts(dialog, ".env-item .src")).toEqual([
      "npx -y @modelcontextprotocol/server-github@2025.4.8  GITHUB_PERSONAL_ACCESS_TOKEN=…",
      'uvx mcp-server-time==2026.1.26 "--zone=a b"',
    ]);
    expect(dialog.textContent).not.toContain("github_pat_fake"); // values only when edited
    expect(dialog.querySelectorAll("[data-edit-row]")).toHaveLength(2);
    expect(dialog.querySelectorAll("[data-delete]")).toHaveLength(2);
    expect(q<HTMLDetailsElement>(dialog, "[data-register-form]").open).toBe(false);
    for (const button of dialog.querySelectorAll("button")) expect(button.type).toBe("button");
    expect(toolFetches(api)).toBe(0);
  });

  it("says when there are none, with the register form open", async () => {
    const { dialog } = await open();
    expect(q(dialog, ".env-empty").textContent).toBe("No MCP servers registered.");
    expect(q<HTMLDetailsElement>(dialog, "[data-register-form]").open).toBe(true);
    expect(q(dialog, "summary").textContent).toBe("Register a server");
    expect(
      ["name", "description", "command", "args", "env"].map((f) => reg(dialog, f).placeholder),
    ).toEqual([
      "github",
      "What it's for, in one line",
      "npx",
      "one per line",
      "NAME=value, one per line",
    ]);
  });

  it("escapes what the server holds, in attributes and in the edit fields", async () => {
    const hostile = '"><img src=x></textarea><b>bold</b>';
    const { dialog } = await open(
      fakeApi({ mcpServers: [server("x", hostile, hostile, [hostile], { A: hostile })] }),
    );
    expect(dialog.querySelector("[data-list] img, [data-list] b")).toBeNull();
    expect(q(dialog, ".env-item .desc").title).toBe(hostile);
    q(dialog, "[data-edit-row]").click();
    expect(dialog.querySelector("[data-list] img, [data-list] b")).toBeNull();
    expect(edit(dialog, "description").value).toBe(hostile);
    expect(edit(dialog, "command").value).toBe(hostile);
    expect(edit(dialog, "args").value).toBe(hostile);
    expect(edit(dialog, "env").value).toBe(`A=${hostile}`);
  });

  it("edits a server in its row: saved unchanged, the PUT is the server as listed", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[0]!, "[data-edit-row]").click();
    const row = items(dialog)[0]!;
    expect(row.classList.contains("editing")).toBe(true);
    expect(q<HTMLInputElement>(row, "input[disabled]").value).toBe("github");
    expect(document.activeElement).toBe(edit(dialog, "description"));
    expect(edit(dialog, "args").value).toBe("-y\n@modelcontextprotocol/server-github@2025.4.8");
    expect(edit(dialog, "env").value).toBe("GITHUB_PERSONAL_ACCESS_TOKEN=github_pat_fake");
    q(row, "[data-save]").click();
    await vi.waitFor(() => expect(dialog.querySelector(".editing")).toBeNull());
    const { name, updatedAt, ...body } = github;
    expect(api.mcpCalls().slice(1)).toMatchObject([
      { method: "PUT", path: "/api/mcp-servers/github", body },
      { method: "GET" },
    ]);
    expect(toolFetches(api)).toBe(1);
  });

  it("saves what the row's fields hold, on Save or Enter; Cancel backs out", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[1]!, "[data-edit-row]").click();
    type(edit(dialog, "description"), "Time.");
    q(dialog, "[data-cancel]").click();
    expect(dialog.querySelector(".editing")).toBeNull();
    expect(api.mcpCalls()).toHaveLength(1);

    q(items(dialog)[1]!, "[data-edit-row]").click();
    type(edit(dialog, "description"), " Time. ");
    type(edit(dialog, "args"), "mcp-server-time==2026.2.1\n\n");
    type(edit(dialog, "env"), "TZ=Europe/Copenhagen");
    const key = enter();
    edit(dialog, "command").dispatchEvent(key);
    expect(key.defaultPrevented).toBe(true);
    await vi.waitFor(() => expect(dialog.querySelector(".editing")).toBeNull());
    expect(api.mcpCalls()[1]).toMatchObject({
      method: "PUT",
      path: "/api/mcp-servers/time",
      body: {
        description: "Time.",
        command: "uvx",
        args: ["mcp-server-time==2026.2.1"],
        env: { TZ: "Europe/Copenhagen" },
      },
    });
    expect(texts(dialog, ".env-item .src")[1]).toBe("uvx mcp-server-time==2026.2.1  TZ=…");
    expect(dialog.open).toBe(true);
  });

  it("keeps Save disabled while it's on its way, and an edit begun on another row meanwhile open", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[0]!, "[data-edit-row]").click();
    type(edit(dialog, "description"), "GitHub.");
    api.holdMcpWrites();
    const save = q<HTMLButtonElement>(dialog, "[data-save]");
    save.click();
    expect(save.disabled).toBe(true);
    edit(dialog, "command").dispatchEvent(enter());
    await vi.waitFor(() => expect(api.mcpCalls().some((c) => c.method === "PUT")).toBe(true));
    q(items(dialog)[1]!, "[data-edit-row]").click();
    api.releaseMcpWrites();
    await vi.waitFor(() => expect(texts(dialog, ".env-item .desc")[0]).toBe("GitHub."));
    expect(api.mcpCalls().filter((c) => c.method === "PUT")).toHaveLength(1);
    expect(items(dialog)[1]!.classList.contains("editing")).toBe(true);
  });

  it("leaves Enter in a text box to make a new line", async () => {
    const { dialog } = await open(listed());
    q(items(dialog)[0]!, "[data-edit-row]").click();
    const key = enter();
    edit(dialog, "args").dispatchEvent(key);
    expect(key.defaultPrevented).toBe(false);
    const key2 = enter();
    reg(dialog, "env").dispatchEvent(key2);
    expect(key2.defaultPrevented).toBe(false);
  });

  it("flags an edit that can't be sent, and shows the server's refusal on the field", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[0]!, "[data-edit-row]").click();
    const save = q<HTMLButtonElement>(dialog, "[data-save]");
    type(edit(dialog, "env"), "TOKEN");
    expect(edit(dialog, "env").classList.contains("bad")).toBe(true);
    expect(q(dialog, "[data-problem]").textContent).toBe("Env lines are NAME=value.");
    expect(save.disabled).toBe(true);
    type(edit(dialog, "env"), "");
    type(edit(dialog, "command"), " ");
    expect(q(dialog, "[data-problem]").textContent).toBe("");
    expect(save.disabled).toBe(true);
    type(edit(dialog, "command"), "npx");
    expect(save.disabled).toBe(false);

    api.failMcp(
      400,
      "RESERVED_ENV_NAME",
      "'MCP_X' is set by ARM for its agents and can't be given.",
    );
    save.click();
    await vi.waitFor(() =>
      expect(q(dialog, "[data-problem]").textContent).toBe(
        "'MCP_X' is set by ARM for its agents and can't be given.",
      ),
    );
    expect(dialog.querySelector(".editing")).not.toBeNull(); // the edit stays
    expect(edit(dialog, "env").classList.contains("bad")).toBe(true);
    expect(edit(dialog, "command").value).toBe("npx");
    type(edit(dialog, "env"), "B=1"); // typing again clears it
    expect(q(dialog, "[data-problem]").textContent).toBe("");
    expect(edit(dialog, "env").classList.contains("bad")).toBe(false);
  });

  it("keeps the field being typed in focused through a list refresh", async () => {
    const { dialog, registry } = await open(listed());
    q(items(dialog)[0]!, "[data-edit-row]").click();
    const args = edit(dialog, "args") as HTMLTextAreaElement;
    args.focus();
    type(args, "-y\npkg@2");
    args.setSelectionRange(3, 5);
    await registry.load();
    const again = edit(dialog, "args") as HTMLTextAreaElement;
    expect(again).not.toBe(args);
    expect(again.value).toBe("-y\npkg@2");
    expect(document.activeElement).toBe(again);
    expect([again.selectionStart, again.selectionEnd]).toEqual([3, 5]);
  });

  it("asks in the row before deleting; Keep backs out", async () => {
    const { dialog, api } = await open(listed());
    q(items(dialog)[1]!, "[data-delete]").click();
    const row = items(dialog)[1]!;
    expect(row.classList.contains("confirming")).toBe(true);
    expect(q(row, ".confirm span").textContent).toBe(
      "Delete time? New sessions can't name it; existing ones keep theirs.",
    );
    q(row, "[data-keep]").click();
    expect(dialog.querySelector(".confirming")).toBeNull();

    q(items(dialog)[1]!, "[data-delete]").click();
    q(dialog, "[data-confirm-delete]").click();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(1));
    expect(api.mcpCalls().slice(1)).toMatchObject([
      { method: "DELETE", path: "/api/mcp-servers/time" },
      { method: "GET" },
    ]);
    expect(toolFetches(api)).toBe(1);
  });

  it("enables Register with a name, a description and a command, checking them inline, and registers", async () => {
    const { dialog, api } = await open(listed());
    const register = q<HTMLButtonElement>(dialog, "[data-register-submit]");
    const err = q(dialog, "[data-register-err]");
    expect(register.disabled).toBe(true);
    type(reg(dialog, "name"), "fetch");
    type(reg(dialog, "description"), "Fetches web pages.");
    expect(register.disabled).toBe(true); // no command yet
    type(reg(dialog, "command"), "uvx");
    expect(register.disabled).toBe(false);
    expect(err.hidden).toBe(true);

    for (const [name, problem] of [
      [
        "Fetch",
        "Names are lowercase letters, digits and -, starting with a letter, at most 32 characters.",
      ],
      ["arm-fetch", "arm and names starting with arm- are reserved for ARM."],
      ["github", "github is already registered: change it in the list."],
    ] as const) {
      type(reg(dialog, "name"), name);
      expect(err.textContent).toBe(problem);
      expect(reg(dialog, "name").classList.contains("bad")).toBe(true);
      expect(register.disabled).toBe(true);
    }
    type(reg(dialog, "name"), "fetch");
    type(reg(dialog, "env"), "A=1\nA=2");
    expect(err.textContent).toBe("A is given twice.");
    expect(reg(dialog, "env").classList.contains("bad")).toBe(true);
    expect(reg(dialog, "name").classList.contains("bad")).toBe(false);
    expect(register.disabled).toBe(true);

    type(reg(dialog, "env"), "A=1");
    type(reg(dialog, "args"), "mcp-server-fetch==2026.1.1");
    register.click();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(3));
    expect(api.mcpCalls()[1]).toMatchObject({
      method: "PUT",
      path: "/api/mcp-servers/fetch",
      body: {
        description: "Fetches web pages.",
        command: "uvx",
        args: ["mcp-server-fetch==2026.1.1"],
        env: { A: "1" },
      },
    });
    await vi.waitFor(() => expect(reg(dialog, "name").value).toBe(""));
    for (const field of ["description", "command", "args", "env"])
      expect(reg(dialog, field).value).toBe("");
    expect(register.disabled).toBe(true);
    expect(document.activeElement).toBe(reg(dialog, "name"));
    expect(toolFetches(api)).toBe(1);
  });

  it("registers on Enter without closing the dialog, and shows the server's refusal on its field", async () => {
    const { dialog, api } = await open();
    type(reg(dialog, "name"), "github");
    type(reg(dialog, "description"), "GitHub.");
    type(reg(dialog, "command"), "npx");
    type(reg(dialog, "env"), "MCP_X=1");
    api.failMcp(
      400,
      "RESERVED_ENV_NAME",
      "'MCP_X' is set by ARM for its agents and can't be given.",
    );
    const key = enter();
    reg(dialog, "command").dispatchEvent(key);
    expect(key.defaultPrevented).toBe(true);
    await vi.waitFor(() =>
      expect(q(dialog, "[data-register-err]").textContent).toBe(
        "'MCP_X' is set by ARM for its agents and can't be given.",
      ),
    );
    expect(reg(dialog, "env").classList.contains("bad")).toBe(true);
    expect(reg(dialog, "name").classList.contains("bad")).toBe(false);
    expect(q<HTMLButtonElement>(dialog, "[data-register-submit]").disabled).toBe(false);
    expect(dialog.open).toBe(true);
    expect(reg(dialog, "name").value).toBe("github");
    expect(toolFetches(api)).toBe(1); // a refused change is fetched again too

    type(reg(dialog, "env"), ""); // typing again clears it
    expect(q(dialog, "[data-register-err]").hidden).toBe(true);
    expect(reg(dialog, "env").classList.contains("bad")).toBe(false);

    const submit = new Event("submit", { cancelable: true });
    q(dialog, "form").dispatchEvent(submit);
    expect(submit.defaultPrevented).toBe(true);
  });

  it("keeps Register disabled while one is in flight", async () => {
    const { dialog, api, registry } = await open();
    const register = q<HTMLButtonElement>(dialog, "[data-register-submit]");
    type(reg(dialog, "name"), "time");
    type(reg(dialog, "description"), "Time.");
    type(reg(dialog, "command"), "uvx");
    api.holdMcpWrites();
    register.click();
    expect(register.disabled).toBe(true);
    await vi.waitFor(() => expect(api.mcpCalls().some((c) => c.method === "PUT")).toBe(true));
    await registry.load(); // a refresh while the PUT is on its way
    expect(register.disabled).toBe(true);
    api.releaseMcpWrites();
    await vi.waitFor(() => expect(items(dialog)).toHaveLength(1));
    expect(api.mcpCalls().filter((c) => c.method === "PUT")).toHaveLength(1);
  });

  it("shows a failed fetch", async () => {
    const api = fakeApi();
    api.failMcp(401, "UNAUTHORIZED", "");
    const { dialog } = await open(api);
    expect(q(dialog, "[data-problem]").textContent).toBe(
      "Couldn't load the MCP servers: Not authorized — try logging in again.",
    );
  });
});
