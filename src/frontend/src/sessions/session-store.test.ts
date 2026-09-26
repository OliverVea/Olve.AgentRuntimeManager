import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, session } from "../testing/fake-api.js";
import { SessionStore } from "./session-store.js";

const at = "2026-09-26T10:05:00Z";
const stores: SessionStore[] = [];
afterEach(() => {
  for (const store of stores.splice(0)) store.stop();
});

function start(api: ReturnType<typeof fakeApi>) {
  const store = new SessionStore(api.client, { reconnectDelayMs: 0 });
  stores.push(store);
  store.start();
  return store;
}

const ids = (sessions: { id: string; status: string }[]) =>
  sessions.map((s) => `${s.id}:${s.status}`);

async function connected(api: ReturnType<typeof fakeApi>, store: SessionStore) {
  await vi.waitFor(() => expect(store.overview).toBe("ready"));
  await vi.waitFor(() => expect(store.stream).toBe("connected"));
  api.push({ type: "heartbeat", at });
}

describe("SessionStore", () => {
  it("loads the queued and working sessions, oldest first, numbering the queue", async () => {
    const api = fakeApi({
      sessions: [
        session("new-q", { status: "queued", createdAt: "2026-09-26T10:03:00Z" }),
        session("old-q", { status: "queued", createdAt: "2026-09-26T10:02:00Z" }),
        session("w", { createdAt: "2026-09-26T10:01:00Z" }),
        session("done", { status: "completed" }),
      ],
    });
    const store = start(api);
    await connected(api, store);

    expect(ids(store.active)).toEqual(["w:working", "old-q:queued", "new-q:queued"]);
    expect(store.active.map((s) => s.queuePosition)).toEqual([undefined, 1, 2]);
    expect(api.searches()[0]?.body).toEqual({ status: ["queued", "working"], limit: 100 });
    expect(decodeURIComponent(api.calls.find((c) => c.path === "/api/events")!.search)).toBe(
      "?event=session.*",
    );
  });

  it("applies what happens while the snapshot loads on top of it", async () => {
    const api = fakeApi({ sessions: [session("s1", { status: "queued" })] });
    api.holdSearch();
    const store = start(api);
    await vi.waitFor(() => expect(store.stream).toBe("connected"));

    api.push({
      type: "session.created",
      at,
      sessionId: "s2",
      session: session("s2", { createdAt: at }),
    });
    api.push({
      type: "session.started",
      at,
      sessionId: "s1",
      previous: "queued",
      providerSessionId: "p",
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(store.active).toEqual([]); // held back until the snapshot is in

    api.releaseSearch();
    await vi.waitFor(() => expect(ids(store.active)).toEqual(["s1:working", "s2:working"]));
  });

  it("ignores a repeated create and events that would move a session backwards", async () => {
    const api = fakeApi({ sessions: [session("s1")] });
    const store = start(api);
    await connected(api, store);

    api.push({
      type: "session.created",
      at,
      sessionId: "s1",
      session: session("s1", { status: "queued" }),
    });
    api.push({ type: "session.queued", at, sessionId: "s1", position: 3 });
    api.push({ type: "session.completed", at, sessionId: "s1", previous: "working", exitCode: 0 });
    api.push({
      type: "session.started",
      at,
      sessionId: "s1",
      previous: "queued",
      providerSessionId: "p",
    });
    api.push({ type: "session.failed", at, sessionId: "s1", previous: "working", error: "late" });
    await vi.waitFor(() => expect(ids(store.ended)).toEqual(["s1:completed"]));
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(ids(store.ended)).toEqual(["s1:completed"]);
    expect(store.active).toEqual([]);
  });

  it("moves a session that ends to History, with its outcome", async () => {
    const api = fakeApi({ sessions: [session("s1"), session("s2")] });
    const store = start(api);
    await connected(api, store);

    api.push({
      type: "session.killed",
      at,
      sessionId: "s1",
      previous: "working",
      source: "user",
      reason: "stop",
      caller: "oliver",
    });
    api.push({
      type: "session.completed",
      at,
      sessionId: "s2",
      previous: "working",
      exitCode: 0,
      summary: "done",
    });

    await vi.waitFor(() => expect(store.active).toEqual([]));
    expect(store.get("s1")).toMatchObject({
      status: "killed",
      endedAt: at,
      killSource: "user",
      killReason: "stop",
      killCaller: "oliver",
    });
    expect(store.get("s2")).toMatchObject({ status: "completed", exitCode: 0, summary: "done" });
  });

  it("pages History, newest first, merging what ended live", async () => {
    const ended = Array.from({ length: 25 }, (_, i) =>
      session(`e${i}`, {
        status: "completed",
        createdAt: new Date(Date.UTC(2026, 8, 25, 0, i)).toISOString(),
      }),
    );
    const api = fakeApi({ sessions: ended });
    const store = start(api);
    await connected(api, store);

    await store.loadHistory();
    expect(store.ended).toHaveLength(20);
    expect(store.ended[0]?.id).toBe("e24");
    expect([store.historyLoaded, store.historyTotal]).toEqual([20, 25]);
    expect(api.searches().at(-1)?.body).toEqual({
      status: ["completed", "cancelled", "killed", "failed"],
      limit: 20,
      offset: 0,
    });

    await store.loadHistory(true);
    expect(store.ended).toHaveLength(25);
    expect(api.searches().at(-1)?.body).toMatchObject({ offset: 20 });
  });

  it("reconnects with a fresh snapshot when the server ends the stream", async () => {
    const api = fakeApi({ sessions: [session("s1")] });
    const store = start(api);
    await connected(api, store);
    const states: string[] = [];
    store.addEventListener("change", () => states.push(store.stream));

    api.sessions.splice(0, 1, session("s1", { status: "completed" })); // ended while we were away
    api.endStream();

    await vi.waitFor(() => expect(api.streams()).toBe(2));
    await vi.waitFor(() => expect(store.active).toEqual([]));
    expect(store.stream).toBe("connected");
    expect(states).toContain("reconnecting");
    expect(api.searches()).toHaveLength(2);
  });

  it("reports the API's error message", async () => {
    const api = fakeApi({
      search: () =>
        Response.json(
          { error: { code: "INVALID_REQUEST", message: "limit must be 1–100", details: {} } },
          { status: 400 },
        ),
    });
    const store = start(api);
    await vi.waitFor(() => expect(store.overview).toBe("error"));
    expect(store.error).toBe("limit must be 1–100");
  });

  it("calls a 401 what it is", async () => {
    const store = start(fakeApi({ search: () => new Response(null, { status: 401 }) }));
    await vi.waitFor(() => expect(store.overview).toBe("error"));
    expect(store.error).toMatch(/Not authorized/);
  });

  it("forgets everything when stopped", async () => {
    const api = fakeApi({ sessions: [session("s1")] });
    const store = start(api);
    await connected(api, store);

    store.stop();

    expect(store.active).toEqual([]);
    expect(store.overview).toBe("idle");
  });
});
