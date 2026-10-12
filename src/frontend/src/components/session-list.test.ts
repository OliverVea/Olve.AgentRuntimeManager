import type { ActivityLine } from "@arm/client";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { ActivityStore } from "../sessions/activity-store.js";
import { clock } from "../sessions/format.js";
import { SessionStore } from "../sessions/session-store.js";
import { fakeApi, session } from "../testing/fake-api.js";
import { SessionList } from "./session-list.js";

beforeAll(() => customElements.define(SessionList.tagName, SessionList));

const cleanup: Array<() => void> = [];
afterEach(() => {
  for (const undo of cleanup.splice(0)) undo();
});

async function mount(api: ReturnType<typeof fakeApi>) {
  const store = new SessionStore(api.client);
  store.start();
  const el = document.createElement(SessionList.tagName) as SessionList;
  el.store = store;
  document.body.append(el);
  cleanup.push(() => {
    el.remove();
    store.stop();
  });
  await vi.waitFor(() => expect(store.overview).toBe("ready"));
  return { el, store };
}

const cards = (el: SessionList) => [...el.shadowRoot!.querySelectorAll<HTMLElement>(".card")];
const text = (el: SessionList, selector: string) =>
  [...el.shadowRoot!.querySelectorAll(selector)].map((e) => e.textContent?.trim());

describe("<session-list>", () => {
  it("shows the Overview as cards: status · time · queue place, the task, id · model · caller", async () => {
    const { el } = await mount(
      fakeApi({
        sessions: [
          session("aaaaaaaa-0001", { caller: "oribot", createdAt: "2026-09-26T10:00:00Z" }),
          session("bbbbbbbb-0002", {
            status: "queued",
            createdAt: "2026-09-26T10:01:00Z",
            model: "opus",
            provider: "claude",
          }),
        ],
      }),
    );

    expect(cards(el).map((c) => c.dataset.open)).toEqual(["aaaaaaaa-0001", "bbbbbbbb-0002"]);
    expect(text(el, ".badge")).toEqual(["working", "queued"]);
    expect(text(el, ".note")).toEqual(["#1 in queue"]);
    expect(text(el, ".task")).toEqual(["prompt of aaaaaaaa-0001", "prompt of bbbbbbbb-0002"]);
    expect(text(el, ".info")).toEqual(["aaaaaaaafake (fake)oribot", "bbbbbbbbopus (claude)tester"]);
    expect(el.shadowRoot!.querySelector("[data-kill]")?.getAttribute("title")).toBe("Kill session");
    expect(el.shadowRoot!.querySelectorAll("[data-kill]")[1]?.getAttribute("title")).toBe(
      "Cancel session",
    );
  });

  it("says what a queued session waits for while its provider is paused, and which attempt is next", async () => {
    const until = "2026-09-26T22:00:00Z";
    const { el } = await mount(
      fakeApi({
        sessions: [
          session("aaaaaaaa-0001", { status: "queued", provider: "claude", model: "sonnet" }),
          session("bbbbbbbb-0002", {
            status: "queued",
            provider: "claude",
            model: "haiku",
            createdAt: "2026-09-26T10:01:00Z",
            attempts: 1,
            error: "API Error: 529 Overloaded",
          }),
          session("cccccccc-0003", { status: "queued", createdAt: "2026-09-26T10:02:00Z" }),
        ],
        providers: [
          { provider: "claude", status: "limited", reason: "session limit", since: until, until },
          { provider: "fake", status: "available" },
        ],
      }),
    );

    const hm = clock(until, Date.now());
    expect(text(el, ".note.paused")).toEqual([
      `waiting for claude: its usage limit resets at ${hm}`,
      `waiting for claude: its usage limit resets at ${hm}`,
    ]);
    expect(el.shadowRoot!.querySelectorAll(".note.paused")[1]?.getAttribute("title")).toBe(
      "Last attempt: API Error: 529 Overloaded",
    );
    expect(text(el, ".attempt")).toEqual(["attempt 2 of 4"]);
  });

  it("shows History with each outcome and a delete button", async () => {
    const { el } = await mount(
      fakeApi({
        sessions: [
          session("c", {
            status: "completed",
            exitCode: 0,
            endedAt: "2026-09-26T10:05:00Z",
            createdAt: "2026-09-26T10:04:00Z",
          }),
          session("k", {
            status: "killed",
            killSource: "user",
            killCaller: "oliver",
            endedAt: "2026-09-26T10:05:00Z",
            createdAt: "2026-09-26T10:03:00Z",
          }),
          session("t", {
            status: "cancelled",
            killSource: "timeout",
            endedAt: "2026-09-26T10:05:00Z",
            createdAt: "2026-09-26T10:02:00Z",
          }),
          session("f", {
            status: "failed",
            error: "out of tokens",
            endedAt: "2026-09-26T10:05:00Z",
            createdAt: "2026-09-26T10:01:00Z",
          }),
        ],
      }),
    );

    el.view = "history";

    await vi.waitFor(() => expect(cards(el)).toHaveLength(4));
    expect(text(el, ".note")).toEqual(["exit 0", "by oliver", "by timeout", "out of tokens"]);
    expect(el.shadowRoot!.querySelectorAll("[data-delete]")).toHaveLength(4);
  });

  it("offers older History while the server has more", async () => {
    const sessions = Array.from({ length: 21 }, (_, i) =>
      session(`e${i}`, {
        status: "completed",
        createdAt: new Date(Date.UTC(2026, 8, 25, 0, i)).toISOString(),
      }),
    );
    const { el } = await mount(fakeApi({ sessions }));
    el.view = "history";
    await vi.waitFor(() => expect(cards(el)).toHaveLength(20));

    el.shadowRoot!.querySelector<HTMLButtonElement>("[data-older]")!.click();

    await vi.waitFor(() => expect(cards(el)).toHaveLength(21));
    expect(el.shadowRoot!.querySelector("[data-older]")).toBeNull();
  });

  it("follows the store live", async () => {
    const api = fakeApi({ sessions: [] });
    const { el } = await mount(api);
    expect(text(el, ".empty")).toEqual(["Nothing running. Start a session above."]);
    await vi.waitFor(() => expect(api.streams()).toBe(1));

    api.push({
      type: "session.created",
      at: "2026-09-26T10:00:00Z",
      sessionId: "s1",
      session: session("s1"),
    });

    await vi.waitFor(() => expect(cards(el)).toHaveLength(1));
  });

  it("reports what the user asks for", async () => {
    const { el } = await mount(fakeApi({ sessions: [session("s1")] }));
    const seen: string[] = [];
    for (const type of ["open-session", "kill-session", "copy-id", "toggle-times"]) {
      el.addEventListener(type, (e) => seen.push(`${type}:${(e as CustomEvent).detail ?? ""}`));
    }
    const root = el.shadowRoot!;

    root.querySelector<HTMLElement>(".task")!.click();
    root.querySelector<HTMLElement>("[data-kill]")!.click();
    root.querySelector<HTMLElement>("[data-copy]")!.click();
    root.querySelector<HTMLElement>("[data-dur]")!.click();
    const card = root.querySelector<HTMLElement>(".card")!;
    card.dispatchEvent(
      new KeyboardEvent("keydown", { key: "Delete", bubbles: true, composed: true }),
    );
    card.dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", bubbles: true, composed: true }),
    );

    expect(seen).toEqual([
      "open-session:s1",
      "kill-session:s1",
      "copy-id:s1",
      "toggle-times:",
      "kill-session:s1",
      "open-session:s1",
    ]);
  });
});

describe("<session-list>'s log column (M5f)", () => {
  const lines: ActivityLine[] = [
    { kind: "text", text: "Looking at the last 20 CI runs." },
    { kind: "tool", text: "Bash → gh run list --limit 20" },
    { kind: "error", text: "fatal: denied" },
    { kind: "text", text: "See https://example.com/runs/1182." },
    { kind: "tool", text: "Read → src/spec/main.tsp" },
  ];

  /** A list with its activity read every 20 ms. */
  async function mountWithActivity(api: ReturnType<typeof fakeApi>) {
    const { el, store } = await mount(api);
    const activity = new ActivityStore(api.client, store, { intervalMs: 20 });
    el.activity = activity;
    activity.start();
    cleanup.push(() => activity.stop());
    return { el, store, activity };
  }

  const log = (el: SessionList, id: string) =>
    el.shadowRoot!.querySelector<HTMLElement>(`[data-open="${id}"] .log`);
  const shown = (el: SessionList, id: string) =>
    [...(log(el, id)?.querySelectorAll<HTMLElement>("[data-kind]") ?? [])].map(
      (l) => `${l.dataset.kind}: ${l.textContent}`,
    );
  const button = (el: SessionList, selector: string) =>
    el.shadowRoot!.querySelector<HTMLButtonElement>(selector)!;

  it("shows a working session's last 3 lines by kind, with links; queued cards keep the column empty", async () => {
    const api = fakeApi({
      sessions: [
        session("w"),
        session("q", { status: "queued", createdAt: "2026-09-26T10:01:00Z" }),
      ],
      activity: (id) => (id === "w" ? lines : undefined),
    });
    const { el } = await mountWithActivity(api);

    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(3));
    expect(shown(el, "w")).toEqual([
      "error: fatal: denied",
      "text: See https://example.com/runs/1182.",
      "tool: Read → src/spec/main.tsp",
    ]);
    const link = log(el, "w")!.querySelector("a")!;
    expect(link.getAttribute("href")).toBe("https://example.com/runs/1182");
    expect(link.getAttribute("target")).toBe("_blank");
    expect(log(el, "w")!.querySelector("[data-kind=tool]")!.getAttribute("title")).toBe(
      "Read → src/spec/main.tsp",
    );
    expect(el.shadowRoot!.querySelector('[data-open="q"] .logcol')!.innerHTML).toBe("");
    expect(api.activityReads("q")).toHaveLength(0);
    expect(button(el, '[data-expand="q"]').classList.contains("off")).toBe(true);
    expect(button(el, '[data-wrap="q"]').classList.contains("off")).toBe(true);
  });

  it("says when the agent has no output yet", async () => {
    const { el } = await mountWithActivity(
      fakeApi({ sessions: [session("w")], activity: () => [] }),
    );

    await vi.waitFor(() => expect(text(el, ".log .quiet")).toEqual(["no output yet"]));
    expect(button(el, '[data-expand="w"]').classList.contains("off")).toBe(true);
  });

  it("expand shows every line read, wrap wraps them (and shows up to 8); each card on its own", async () => {
    const { el } = await mountWithActivity(
      fakeApi({ sessions: [session("w")], activity: () => lines }),
    );
    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(3));
    expect(button(el, '[data-expand="w"]').getAttribute("title")).toBe(
      "Show more of the log (up to 20 lines)",
    );

    button(el, '[data-expand="w"]').click();
    expect(shown(el, "w")).toHaveLength(5);
    expect(log(el, "w")!.classList.contains("open")).toBe(true);
    expect(button(el, '[data-expand="w"]').classList.contains("on")).toBe(true);
    expect(button(el, '[data-expand="w"]').getAttribute("title")).toBe("Show the last 3 lines");

    button(el, '[data-expand="w"]').click();
    button(el, '[data-wrap="w"]').click();
    expect(log(el, "w")!.classList.contains("wrap")).toBe(true);
    expect(log(el, "w")!.classList.contains("open")).toBe(false);
    expect(shown(el, "w")).toHaveLength(5);
  });

  it("Options' defaults apply to every card, again", async () => {
    const { el } = await mountWithActivity(
      fakeApi({ sessions: [session("w")], activity: () => lines }),
    );
    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(3));
    button(el, '[data-wrap="w"]').click();

    el.logDefaults = { wrap: false, expand: true };

    expect(log(el, "w")!.classList.contains("wrap")).toBe(false);
    expect(shown(el, "w")).toHaveLength(5);
  });

  it("a click in the log column doesn't open the session", async () => {
    const { el } = await mountWithActivity(
      fakeApi({ sessions: [session("w")], activity: () => lines }),
    );
    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(3));
    const opened = vi.fn();
    el.addEventListener("open-session", opened);

    log(el, "w")!.querySelector<HTMLElement>("[data-kind=tool]")!.click();
    el.shadowRoot!.querySelector<HTMLElement>(".task")!.click();

    expect(opened).toHaveBeenCalledTimes(1);
  });

  it("re-reads while the session works; once it has ended its card has no log and reads stop", async () => {
    let said = "first";
    const api = fakeApi({
      sessions: [session("s1")],
      activity: () => [{ kind: "text", text: said }],
    });
    const { el } = await mountWithActivity(api);
    await vi.waitFor(() => expect(shown(el, "s1")).toEqual(["text: first"]));
    said = "second";
    await vi.waitFor(() => expect(shown(el, "s1")).toEqual(["text: second"]));
    await vi.waitFor(() => expect(api.streams()).toBe(1));

    api.push({
      type: "session.completed",
      at: "2026-09-26T10:05:00Z",
      sessionId: "s1",
      previous: "working",
      exitCode: 0,
    });
    el.view = "history";

    await vi.waitFor(() => expect(text(el, ".note")).toEqual(["exit 0"]));
    expect(el.shadowRoot!.querySelector(".logcol")).toBeNull();
    expect(el.shadowRoot!.querySelector("[data-expand], [data-wrap]")).toBeNull();
    const reads = api.activityReads().length;
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(api.activityReads().length).toBeLessThanOrEqual(reads + 1);
  });

  it("asks for 3 lines, 8 for a wrapped card and 20 for an expanded one", async () => {
    const api = fakeApi({ sessions: [session("w")], activity: () => lines.slice(0, 3) });
    const { el } = await mountWithActivity(api);
    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(3));
    expect(new Set(api.activityReads("w").map((c) => c.search))).toEqual(new Set(["?limit=3"]));
    // A full box may have more behind it.
    expect(button(el, '[data-expand="w"]').classList.contains("off")).toBe(false);

    button(el, '[data-wrap="w"]').click();
    await vi.waitFor(() => expect(api.activityReads("w").at(-1)?.search).toBe("?limit=8"));
    button(el, '[data-expand="w"]').click();
    await vi.waitFor(() => expect(api.activityReads("w").at(-1)?.search).toBe("?limit=20"));
  });

  it("new lines update only that card's log, keeping focus and the other cards", async () => {
    let said = "first";
    const api = fakeApi({
      sessions: [session("a"), session("b", { createdAt: "2026-09-26T10:01:00Z" })],
      activity: (id) => [{ kind: "text", text: id === "a" ? said : "steady" }],
    });
    const { el } = await mountWithActivity(api);
    await vi.waitFor(() => expect(shown(el, "a")).toEqual(["text: first"]));
    await vi.waitFor(() => expect(shown(el, "b")).toEqual(["text: steady"]));
    const cardA = el.shadowRoot!.querySelector('[data-open="a"]');
    const logB = log(el, "b");
    button(el, '[data-wrap="b"]').focus();
    expect(el.shadowRoot!.activeElement?.getAttribute("data-wrap")).toBe("b");

    said = "second";

    await vi.waitFor(() => expect(shown(el, "a")).toEqual(["text: second"]));
    expect(el.shadowRoot!.querySelector('[data-open="a"]')).toBe(cardA);
    expect(log(el, "b")).toBe(logB);
    expect(el.shadowRoot!.activeElement?.getAttribute("data-wrap")).toBe("b");
  });

  it("Options saved without a change to the log defaults keep each card's own choice", async () => {
    const { el } = await mountWithActivity(
      fakeApi({ sessions: [session("w")], activity: () => lines }),
    );
    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(3));
    button(el, '[data-wrap="w"]').click();

    el.logDefaults = { wrap: false, expand: false };

    expect(log(el, "w")!.classList.contains("wrap")).toBe(true);
  });

  it("a card's own choice goes when its session ends: continued, it starts from the defaults", async () => {
    const api = fakeApi({ sessions: [session("s1")], activity: () => lines });
    const { el } = await mountWithActivity(api);
    await vi.waitFor(() => expect(shown(el, "s1")).toHaveLength(3));
    await vi.waitFor(() => expect(api.streams()).toBe(1));
    button(el, '[data-wrap="s1"]').click();
    expect(log(el, "s1")!.classList.contains("wrap")).toBe(true);
    const at = "2026-09-26T10:05:00Z";

    api.push({ type: "session.completed", at, sessionId: "s1", previous: "working", exitCode: 0 });
    await vi.waitFor(() => expect(cards(el)).toHaveLength(0));
    api.push({ type: "session.queued", at, sessionId: "s1", position: 1, previous: "completed" });
    api.push({
      type: "session.started",
      at,
      sessionId: "s1",
      previous: "queued",
      providerSessionId: "p",
    });

    await vi.waitFor(() => expect(shown(el, "s1")).toHaveLength(3));
    expect(log(el, "s1")!.classList.contains("wrap")).toBe(false);
  });

  it("links: not one cut short by the bound, and not past a quote or '>'", async () => {
    const { el } = await mountWithActivity(
      fakeApi({
        sessions: [session("w")],
        activity: () => [
          { kind: "text", text: "Opened https://example.com/a-very-long-pa…" },
          { kind: "text", text: 'See "https://example.com/q?a=1&b=2" and <https://example.com/x>' },
        ],
      }),
    );
    await vi.waitFor(() => expect(shown(el, "w")).toHaveLength(2));

    const hrefs = [...log(el, "w")!.querySelectorAll("a")].map((a) => a.getAttribute("href"));
    expect(hrefs).toEqual(["https://example.com/q?a=1&b=2", "https://example.com/x"]);
  });
});
