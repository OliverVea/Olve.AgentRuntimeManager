import type { Conversation } from "@arm/client";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { defaultModels, session } from "../testing/fake-api.js";
import { SessionPage } from "./session-page.js";

beforeAll(() => customElements.define(SessionPage.tagName, SessionPage));

const pages: SessionPage[] = [];
afterEach(() => {
  for (const p of pages.splice(0)) p.remove();
});

const conversation: Conversation = {
  entries: [
    { seq: 1, kind: "prompt", at: "2026-09-27T15:00:00Z", text: "Fix the **bug**" },
    { seq: 2, kind: "thinking", at: "2026-09-27T15:00:02Z", text: "Look first." },
    {
      seq: 3,
      kind: "tool_call",
      at: "2026-09-27T15:00:05Z",
      tool: "Bash",
      toolId: "t1",
      input: { command: "cd /w && ls" },
    },
    {
      seq: 4,
      kind: "tool_result",
      at: "2026-09-27T15:00:06.200Z",
      toolId: "t1",
      text: "a.txt\nb.txt",
      isError: false,
    },
    {
      seq: 5,
      kind: "tool_call",
      at: "2026-09-27T15:00:07Z",
      tool: "Bash",
      toolId: "t2",
      input: { command: "git push" },
    },
    {
      seq: 6,
      kind: "tool_result",
      at: "2026-09-27T15:00:09Z",
      toolId: "t2",
      text: "remote: no\nfatal: denied",
      isError: true,
    },
    { seq: 7, kind: "text", at: "2026-09-27T15:01:45Z", text: "Done." },
    { seq: 8, kind: "turn_end", at: "2026-09-27T15:01:45Z", text: "Done.", isError: false },
  ],
  turns: 1,
  messages: 1,
  toolCalls: 2,
  lastSeq: 8,
};

async function mount(view = { thinking: false, tools: false, notices: false }) {
  const page = document.createElement(SessionPage.tagName) as SessionPage;
  document.body.append(page);
  pages.push(page);
  page.view = view;
  page.session = session("7c1e4a2b-0000-0000-0000-000000000000", {
    status: "completed",
    exitCode: 0,
    providerSessionId: "7c1e4a2b-0000-0000-0000-000000000000",
    startedAt: "2026-09-27T15:00:00Z",
    endedAt: "2026-09-27T15:01:45Z",
  });
  page.conversation = conversation;
  await Promise.resolve();
  return page;
}

const $$ = (page: SessionPage, selector: string) => [
  ...page.shadowRoot!.querySelectorAll<HTMLElement>(selector),
];
const text = (page: SessionPage, selector: string) =>
  $$(page, selector).map((e) => e.textContent?.trim());

describe("<session-page>", () => {
  it("shows the card, the counts and every entry as a box, tool calls with a one-line preview", async () => {
    const page = await mount();

    expect(text(page, ".head .badge")).toEqual(["completed"]);
    expect(text(page, ".counts span")).toEqual(["1 turn", "1 message", "2 tool calls"]);
    expect($$(page, ".msg").map((m) => m.classList[1])).toEqual(["prompt", "thinking", "answer"]);
    expect(page.shadowRoot!.querySelector(".msg.prompt strong")?.textContent).toBe("bug");
    expect(text(page, ".tool .arg")).toEqual(["ls", "git push"]);
    expect(text(page, ".tool .preview")).toEqual(["a.txt", "fatal: denied"]);
    expect(text(page, ".e .t .took")).toEqual(["(1.2s)", "(2.0s)"]);
    // The agent's last text is the turn's answer: shown once.
    expect($$(page, ".msg.text")).toHaveLength(0);
    expect(text(page, ".turn-end")[0]).toContain("Turn ended");
  });

  it("shows the model by its display names, and its ids in Details, once the list has it", async () => {
    const page = await mount();
    const model = () =>
      $$(page, ".meta > div").find((d) => d.querySelector("dt")?.textContent === "model");
    expect(text(page, ".head .info span:nth-child(2)")).toEqual(["fake (fake)"]);
    expect(model()?.querySelector("dd")?.textContent).toBe("fake/fake");

    page.providers = defaultModels;
    await Promise.resolve();
    expect(text(page, ".head .info span:nth-child(2)")).toEqual(["Instant (fake)"]);
    expect(model()?.querySelector("dd")?.textContent).toBe("Instant (fake) fake/fake");
  });

  it("opens errors by default, others when every tool call is open, and keeps what the user opened", async () => {
    const page = await mount();
    const open = () => $$(page, ".tool details").map((d) => (d as HTMLDetailsElement).open);
    expect(open()).toEqual([false, true]);

    const first = $$(page, ".tool details")[0] as HTMLDetailsElement;
    first.open = true;
    first.dispatchEvent(new Event("toggle"));
    page.conversation = { ...conversation };
    await Promise.resolve();
    expect(open()).toEqual([true, true]);
  });

  it("the icons switch thinking and every tool call, and report it", async () => {
    const page = await mount();
    const changes = vi.fn();
    page.addEventListener("view-change", (e) => changes((e as CustomEvent).detail));

    ($$(page, '[data-view="thinking"]')[0] as HTMLElement).click();
    ($$(page, '[data-view="tools"]')[0] as HTMLElement).click();

    expect(changes).toHaveBeenLastCalledWith({ thinking: true, tools: true, notices: false });
    expect(($$(page, ".msg.thinking details")[0] as HTMLDetailsElement).open).toBe(true);
    expect($$(page, ".tool details").every((d) => (d as HTMLDetailsElement).open)).toBe(true);
  });

  it("times read +m:ss into the conversation; clicking one asks to switch", async () => {
    const page = await mount();
    const toggled = vi.fn();
    page.addEventListener("toggle-times", toggled);

    expect(text(page, ".e .t button")[0]).toBe("+0:00");
    expect(text(page, ".e .t button")[1]).toBe("+0:02");
    ($$(page, ".e .t button")[0] as HTMLElement).click();

    expect(toggled).toHaveBeenCalledOnce();
  });

  it("a long result stops at 12 lines until asked for all of it", async () => {
    const page = await mount({ thinking: false, tools: true, notices: false });
    const long = Array.from({ length: 30 }, (_, i) => `line ${i + 1}`).join("\n");
    page.conversation = {
      ...conversation,
      entries: conversation.entries.map((e) =>
        e.toolId === "t1" && e.kind === "tool_result" ? { ...e, text: long } : e,
      ),
    };
    await Promise.resolve();
    const result = () => $$(page, "pre.result")[0]!.textContent!.split("\n").length;
    expect(result()).toBe(12);

    ($$(page, "[data-whole]")[0] as HTMLElement).click();

    expect(result()).toBe(30);
  });

  it("notices are hidden until their icon is on, then show in one line", async () => {
    const page = await mount();
    page.conversation = {
      ...conversation,
      entries: [
        ...conversation.entries,
        {
          seq: 9,
          kind: "notice",
          at: "2026-09-27T15:01:46Z",
          text: '<task-notification><summary>Background command "cd /w &amp;&amp; mise install" completed (exit code 0)</summary></task-notification>',
        },
      ],
    };
    await Promise.resolve();
    expect($$(page, ".msg.notice")).toHaveLength(0);

    ($$(page, '[data-view="notices"]')[0] as HTMLElement).click();

    expect(text(page, ".msg.notice")).toEqual([
      "Background command finished, exit 0: mise install",
    ]);
  });

  it("the message box says what a message will do; a never-started session has none", async () => {
    const page = await mount();
    expect(text(page, ".composer .hint")[0]).toContain("continues the session");

    page.session = session("w", { status: "working", providerSessionId: "p" });
    await Promise.resolve();
    expect(text(page, ".composer .hint")[0]).toContain("after its current step");

    page.session = session("c", { status: "cancelled" });
    await Promise.resolve();
    expect($$(page, ".composer")).toHaveLength(0);
    expect(text(page, ".no-agent")[0]).toContain("never started");
  });

  it("Ctrl+Enter sends; what's typed survives a re-render; a failure keeps it", async () => {
    const page = await mount();
    const sent = vi.fn();
    page.addEventListener("send-message", (e) => sent((e as CustomEvent).detail));
    const box = () => page.shadowRoot!.querySelector<HTMLTextAreaElement>("textarea.message")!;

    box().value = "Push it over SSH";
    box().dispatchEvent(new Event("input", { bubbles: true }));
    page.conversation = { ...conversation }; // the page re-reads a running session
    await Promise.resolve();
    expect(box().value).toBe("Push it over SSH");

    box().dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", ctrlKey: true, bubbles: true }),
    );
    expect(sent).toHaveBeenCalledWith({
      id: "7c1e4a2b-0000-0000-0000-000000000000",
      text: "Push it over SSH",
    });

    page.sendFailed("Couldn't send: offline.");
    await Promise.resolve();
    expect(box().value).toBe("Push it over SSH");
    expect(text(page, ".send-problem")).toEqual(["Couldn't send: offline."]);
  });

  it("a sent message is labelled with its delivery once it's in the conversation; held ones wait below", async () => {
    const page = await mount();
    page.sent({ text: "Do it again", delivery: "continued", caller: "oliver" });
    page.sent({ text: "And then this", delivery: "pending", caller: "oliver" });
    await Promise.resolve();
    expect(text(page, ".label .delivery")).toEqual([
      "continued · oliver",
      "2 · sent when the agent starts",
    ]);
    expect($$(page, ".msg.prompt.held")).toHaveLength(1);

    page.conversation = {
      ...conversation,
      entries: [
        ...conversation.entries,
        { seq: 9, kind: "prompt", at: "2026-09-27T15:05:00Z", text: "Do it again" },
      ],
    };
    await Promise.resolve();
    // The first is in the conversation now, labelled there; the second is still held.
    expect(text(page, ".label .delivery")).toEqual([
      "continued · oliver",
      "1 · sent when the agent starts",
    ]);
    expect(page.shadowRoot!.querySelector<HTMLTextAreaElement>("textarea.message")!.value).toBe("");
  });

  it("Details has no repos row for a session without repos", async () => {
    const page = await mount();
    expect(text(page, ".meta dt")).not.toContain("repos");
  });

  it("Details shows each repo: its URL, its ref or the default branch, and the folder and branch from the API", async () => {
    const page = await mount();
    page.session = session("d27b5e90-1c3a-4f8e-a471-9b0e6c2d5f38", {
      repos: [
        // Folder and branch as the server returned them, unlike what the URL alone would give:
        // the page shows the API's, it doesn't work them out.
        {
          url: "git@github.com:OliverVea/Olve.AgentRuntimeManager.git",
          folder: "arm-folder",
          branch: "arm/server01",
        },
        {
          url: "https://github.com/OliverVea/Olve.Trains",
          ref: "4c1d9e7",
          folder: "Olve.Trains",
          branch: "arm/server01",
        },
      ],
    });
    await Promise.resolve();

    const row = $$(page, ".meta > div").find(
      (d) => d.querySelector("dt")?.textContent === "repos",
    )!;
    expect(row.className).toBe("full");
    const repos = [...row.querySelectorAll<HTMLElement>(".repo")];
    expect(repos.map((r) => r.querySelector(":scope > .copy")?.textContent)).toEqual([
      "git@github.com:OliverVea/Olve.AgentRuntimeManager.git",
      "https://github.com/OliverVea/Olve.Trains",
    ]);
    expect(
      repos.map((r) => [...r.querySelectorAll(".facts > span")].map((f) => f.textContent)),
    ).toEqual([
      ["ref default branch", "folder arm-folder", "branch arm/server01"],
      ["ref 4c1d9e7", "folder Olve.Trains", "branch arm/server01"],
    ]);
    // URL, folder and branch copy on a click (the ref doesn't), as the id does.
    expect(
      [...repos[1]!.querySelectorAll<HTMLElement>("[data-copy]")].map((c) => c.dataset.copy),
    ).toEqual(["https://github.com/OliverVea/Olve.Trains", "Olve.Trains", "arm/server01"]);
    const copied = vi.fn();
    page.addEventListener("copy-id", (e) => copied((e as CustomEvent<string>).detail));
    repos[0]!.querySelectorAll<HTMLElement>("[data-copy]")[1]!.click();
    expect(copied).toHaveBeenCalledWith("arm-folder");
  });

  it("escapes a repo's URL, ref, folder and branch, attributes included", async () => {
    const page = await mount();
    const evil = '"><img src=x onerror=alert(1)>';
    page.session = session("x", {
      repos: [{ url: `https://h/${evil}`, ref: evil, folder: evil, branch: evil }],
    });
    await Promise.resolve();

    expect(page.shadowRoot!.querySelector("img")).toBeNull();
    const copies = $$(page, ".repo [data-copy]").map((c) => c.dataset.copy);
    expect(copies).toEqual([`https://h/${evil}`, evil, evil]);
    expect(text(page, ".repo .facts .mono")[0]).toBe(evil);
  });

  it("a failed checkout shows its error on the card's first line and whole in Details", async () => {
    const page = await mount();
    const error =
      "Checking out git@github.com:OliverVea/Olve.AgentRuntimeManager.git failed: 'no-such-ref' is not a branch, tag or commit of it.";
    page.session = session("b60e4d13-9a2c-47f5-8e31-c4d7a0f92b68", {
      status: "failed",
      error,
      endedAt: "2026-09-27T15:00:04Z",
      repos: [
        {
          url: "git@github.com:OliverVea/Olve.AgentRuntimeManager.git",
          ref: "no-such-ref",
          folder: "Olve.AgentRuntimeManager",
          branch: "arm/b60e4d13",
        },
      ],
    });
    await Promise.resolve();

    expect(text(page, ".head .note.failed")).toEqual([error]);
    expect(text(page, ".meta .err")).toEqual([error]);
    expect(text(page, ".repo .facts .mono")).toContain("no-such-ref");
  });

  it("an empty conversation says why", async () => {
    const page = await mount();
    page.session = session("x", { status: "queued" });
    page.conversation = { entries: [], turns: 0, messages: 0, toolCalls: 0, lastSeq: 0 };
    await Promise.resolve();

    expect(text(page, ".empty")).toEqual(["Queued: the conversation starts when the agent does."]);
  });
});
