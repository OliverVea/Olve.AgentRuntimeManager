import type {
  ArmEventData,
  EnvVariable,
  ProviderHealth,
  Session,
  SessionSearch,
} from "@arm/client";
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
    // A session starts on its first attempt; a fresh queued one hasn't yet.
    attempts: overrides.status === "queued" ? 0 : 1,
    retriesLeft: 2,
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
 * `providers` is what `GET /api/providers/health` answers (every provider available by default).
 * `env` is the registered variables `/api/env` lists, sets and deletes; `failEnv` makes the next
 * call to `/api/env` answer with the given error envelope instead.
 */
export function fakeApi(
  options: {
    sessions?: Session[];
    search?: () => Response;
    providers?: ProviderHealth[];
    env?: EnvVariable[];
  } = {},
) {
  const calls: Call[] = [];
  const sessions = options.sessions ?? [];
  const env = options.env ?? [];
  let envFailure: Response | undefined;
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
    const byId = url.pathname.match(/^\/api\/sessions\/([^/]+)$/);
    if (request.method === "GET" && byId) {
      const found = sessions.find((s) => s.id === byId[1]);
      return found ? Response.json(found) : new Response(null, { status: 404 });
    }
    if (request.method === "GET" && url.pathname === "/api/providers/health") {
      return Response.json(
        options.providers ?? [
          { provider: "claude", status: "available" },
          { provider: "fake", status: "available" },
        ],
      );
    }
    if (url.pathname === "/api/env" || url.pathname.startsWith("/api/env/")) {
      const failure = envFailure;
      envFailure = undefined;
      if (failure) return failure;
      const name = decodeURIComponent(url.pathname.slice("/api/env/".length));
      const at = env.findIndex((v) => v.name === name);
      if (request.method === "GET") return Response.json(env);
      if (request.method === "PUT") {
        const variable = { name, ...body, updatedAt: "2026-09-27T12:00:00Z" } as EnvVariable;
        if (at >= 0) env[at] = variable;
        else env.push(variable);
        return Response.json(variable);
      }
      if (request.method === "DELETE" && at >= 0) {
        env.splice(at, 1);
        return new Response(null, { status: 204 });
      }
      return Response.json(
        {
          error: {
            code: "ENV_NOT_FOUND",
            message: `No environment variable '${name}' is registered.`,
          },
        },
        { status: 404 },
      );
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
    env,
    envCalls: () => calls.filter((c) => c.path.startsWith("/api/env")),
    failEnv(status: number, code: string, message: string) {
      envFailure = Response.json({ error: { code, message, details: {} } }, { status });
    },
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
