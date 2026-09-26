import type { ArmEventData, Session } from "@arm/client";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { createApiClient } from "../api-client.js";
import { SessionList } from "./session-list.js";

function session(id: string, overrides: Partial<Session> = {}): Session {
  return {
    id,
    status: "working",
    prompt: `prompt of ${id}`,
    provider: "claude",
    tags: {},
    env: {},
    timeoutSeconds: 600,
    tools: [],
    skills: [],
    messaging: true,
    headless: false,
    createdAt: "2026-09-26T10:00:00Z",
    ...overrides,
  };
}

/**
 * A real generated client over a scripted `fetch`: SessionList drives the Hey API SDK, and the
 * fake backend answers by method + path, recording every request (URL, method, body). The event
 * stream stays open; `push` writes one SSE frame to it.
 */
function fakeApi(overrides: { search?: () => Response; sessions?: Session[] } = {}) {
  const calls: { method: string; path: string; search: string; body: unknown }[] = [];
  const sessions = overrides.sessions ?? [];
  const encoder = new TextEncoder();
  let events: ReadableStreamDefaultController<Uint8Array> | undefined;
  let nextId = 1;

  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    calls.push({
      method: request.method,
      path: url.pathname,
      search: url.search,
      body: text ? JSON.parse(text) : undefined,
    });

    if (request.method === "POST" && url.pathname === "/api/sessions/search") {
      return (
        overrides.search?.() ??
        Response.json({ items: sessions, total: sessions.length, limit: 50, offset: 0 })
      );
    }
    if (request.method === "GET" && url.pathname === "/api/events") {
      const body = new ReadableStream<Uint8Array>({
        start: (controller) => {
          events = controller;
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }
    return new Response(null, { status: 404 });
  });

  const push = (event: ArmEventData) => {
    if (!events) throw new Error("event stream not open");
    events.enqueue(
      encoder.encode(`event: ${event.type}\nid: ${nextId++}\ndata: ${JSON.stringify(event)}\n\n`),
    );
  };

  return { client: createApiClient("http://test", { fetch }), calls, push };
}

beforeAll(() => customElements.define(SessionList.tagName, SessionList));

const mounted: SessionList[] = [];
afterEach(() => {
  for (const el of mounted.splice(0)) el.remove(); // closes the event stream
});

/** Mount a signed-in list on `client` and wait for its first page. */
async function mount(client: ReturnType<typeof fakeApi>["client"], signedIn = true) {
  const el = document.createElement(SessionList.tagName) as SessionList;
  el.client = client;
  el.signedIn = signedIn;
  document.body.append(el);
  mounted.push(el);
  if (signedIn) await vi.waitFor(() => expect(el.shadowRoot!.querySelector(".bar")).not.toBeNull());
  await el.load();
  return el;
}

const statuses = (el: SessionList) =>
  [...el.shadowRoot!.querySelectorAll("li")].map(
    (li) => `${li.dataset.id}:${li.querySelector(".status")?.textContent}`,
  );

describe("<session-list>", () => {
  it("renders the newest page of sessions the API returns", async () => {
    const { client, calls } = fakeApi({
      sessions: [
        session("aaaaaaaa-0001", { caller: "oribot", prompt: "x".repeat(300) }),
        session("bbbbbbbb-0002", { status: "queued", queuePosition: 2 }),
      ],
    });

    const el = await mount(client);
    const root = el.shadowRoot!;

    expect(statuses(el)).toEqual(["aaaaaaaa-0001:working", "bbbbbbbb-0002:queued"]);
    expect(root.querySelector(".id")?.textContent).toBe("aaaaaaaa");
    expect(root.querySelector(".caller")?.textContent).toBe("oribot");
    expect(root.querySelector(".provider")?.textContent).toBe("claude");
    expect(root.querySelector(".prompt")?.textContent).toHaveLength(120); // truncated
    expect(root.querySelector(".queue")?.textContent).toBe("#2 in queue");
    expect(root.querySelector(".count")?.textContent).toBe("2 sessions");
    expect(calls.find((c) => c.path === "/api/sessions/search")?.body).toEqual({ limit: 50 });
  });

  it("subscribes to session events and applies them", async () => {
    const api = fakeApi({ sessions: [session("s1", { status: "queued", queuePosition: 1 })] });
    const el = await mount(api.client);

    const stream = await vi.waitFor(() => {
      const call = api.calls.find((c) => c.path === "/api/events");
      expect(call).toBeDefined();
      return call!;
    });
    expect(decodeURIComponent(stream.search)).toBe("?event=session.*");

    const at = "2026-09-26T10:01:00Z";
    api.push({ type: "session.created", at, sessionId: "s2", session: session("s2") });
    api.push({
      type: "session.started",
      at,
      sessionId: "s1",
      previous: "queued",
      providerSessionId: "p1",
    });
    await vi.waitFor(() => expect(statuses(el)).toEqual(["s2:working", "s1:working"]));
    expect(el.shadowRoot!.querySelector(".queue")).toBeNull(); // no longer queued
    expect(el.shadowRoot!.querySelector(".live")?.textContent).toBe("Live");

    api.push({ type: "session.completed", at, sessionId: "s1", previous: "working", exitCode: 0 });
    api.push({ type: "session.failed", at, sessionId: "s2", previous: "working", error: "boom" });
    await vi.waitFor(() => expect(statuses(el)).toEqual(["s2:failed", "s1:completed"]));
    expect(el.shadowRoot!.querySelector(".count")?.textContent).toBe("2 sessions");
  });

  it("shows a sign-in prompt and calls nothing while signed out", async () => {
    const { client, calls } = fakeApi({ sessions: [session("s1")] });
    const el = await mount(client, false);

    expect(el.shadowRoot!.querySelector(".signin")?.textContent).toMatch(/Sign in to see sessions/);
    expect(calls).toHaveLength(0);

    const signIn = vi.fn();
    el.addEventListener("sign-in", signIn);
    el.shadowRoot!.querySelector<HTMLButtonElement>('[data-action="sign-in"]')!.click();
    expect(signIn).toHaveBeenCalledTimes(1);
  });

  it("clears the list when signed out", async () => {
    const el = await mount(fakeApi({ sessions: [session("s1")] }).client);
    expect(statuses(el)).toHaveLength(1);

    el.signedIn = false;

    expect(el.shadowRoot!.querySelector("li")).toBeNull();
    expect(el.shadowRoot!.querySelector(".signin")).not.toBeNull();
  });

  it("surfaces the message from the API's error envelope", async () => {
    const el = await mount(
      fakeApi({
        search: () =>
          Response.json(
            { error: { code: "INVALID_REQUEST", message: "limit must be 1–100", details: {} } },
            { status: 400 },
          ),
      }).client,
    );
    expect(el.shadowRoot!.querySelector(".error")?.textContent).toBe("limit must be 1–100");
  });

  it("surfaces an auth-specific error on 401", async () => {
    const el = await mount(fakeApi({ search: () => new Response(null, { status: 401 }) }).client);
    expect(el.shadowRoot!.querySelector(".error")?.textContent).toMatch(/Not authorized/);
  });
});
