import type { ArmEventData, Session, SessionSearch } from "@arm/client";
import { vi } from "vitest";
import { createApiClient } from "../api-client.js";

export function session(id: string, overrides: Partial<Session> = {}): Session {
  return {
    id,
    status: "working",
    prompt: `prompt of ${id}`,
    provider: "fake",
    model: "fake",
    caller: "tester",
    attempts: 1,
    createdAt: "2026-09-26T10:00:00Z",
    ...overrides,
  };
}

export type Call = { method: string; path: string; search: string; body: unknown };

/**
 * A real generated client over a scripted `fetch`: the code under test drives the Hey API SDK,
 * and this fake backend answers by method + path, recording every request. Search filters
 * `sessions` by status and pages them newest first. Each `GET /api/events` opens a stream that
 * stays open; `push` writes one SSE frame to the latest, `endStream` ends it like a redeploy.
 * `holdSearch` makes searches wait for `releaseSearch`, to deliver events before the snapshot.
 */
export function fakeApi(options: { sessions?: Session[]; search?: () => Response } = {}) {
  const calls: Call[] = [];
  const sessions = options.sessions ?? [];
  const encoder = new TextEncoder();
  let events: ReadableStreamDefaultController<Uint8Array> | undefined;
  let nextId = 1;
  let held: Array<() => void> | undefined;

  const searchResponse = (body: SessionSearch) => {
    if (options.search) return options.search();
    const matches = sessions
      .filter((s) => !body.status || body.status.includes(s.status))
      .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
    const offset = body.offset ?? 0;
    const limit = body.limit ?? 20;
    return Response.json({
      items: matches.slice(offset, offset + limit),
      total: matches.length,
      limit,
      offset,
    });
  };

  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    const body = text ? JSON.parse(text) : undefined;
    calls.push({ method: request.method, path: url.pathname, search: url.search, body });

    if (request.method === "POST" && url.pathname === "/api/sessions/search") {
      if (held) await new Promise<void>((resolve) => held?.push(resolve));
      return searchResponse(body ?? {});
    }
    if (request.method === "GET" && url.pathname === "/api/events") {
      const stream = new ReadableStream<Uint8Array>({
        start: (controller) => {
          events = controller;
        },
      });
      return new Response(stream, { headers: { "Content-Type": "text/event-stream" } });
    }
    return new Response(null, { status: 404 });
  });

  return {
    client: createApiClient("http://test", { fetch }),
    calls,
    sessions,
    streams: () => calls.filter((c) => c.path === "/api/events").length,
    searches: () => calls.filter((c) => c.path === "/api/sessions/search"),
    push(event: ArmEventData) {
      if (!events) throw new Error("event stream not open");
      const id = event.type === "heartbeat" ? "" : `id: ${nextId++}\n`;
      events.enqueue(
        encoder.encode(`event: ${event.type}\n${id}data: ${JSON.stringify(event)}\n\n`),
      );
    },
    endStream() {
      events?.close();
      events = undefined;
    },
    holdSearch() {
      held = [];
    },
    releaseSearch() {
      const waiting = held ?? [];
      held = undefined;
      for (const resolve of waiting) resolve();
    },
  };
}
