import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
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
