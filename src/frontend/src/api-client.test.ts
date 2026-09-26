import { eventsStream, sessionsGet, sessionsSearch } from "@arm/client";
import { describe, expect, it, vi } from "vitest";
import { createApiClient } from "./api-client.js";

/** A fetch that records each request's Authorization header + body, answering from `statuses`. */
function recordingFetch(statuses: number[]) {
  const seen: { auth: string | null; body: string }[] = [];
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    seen.push({ auth: request.headers.get("Authorization"), body: await request.text() });
    const status = statuses[seen.length - 1] ?? 200;
    return status === 200 ? Response.json({ items: [], total: 0 }) : new Response(null, { status });
  });
  return { fetch, seen };
}

describe("createApiClient", () => {
  it("sends no Authorization header without a token", async () => {
    const { fetch, seen } = recordingFetch([200]);
    const client = createApiClient("http://test", { fetch, getToken: async () => null });
    await sessionsGet({ client, path: { id: "s1" } });
    expect(seen[0]?.auth).toBeNull();
  });

  it("attaches the current bearer token", async () => {
    const { fetch, seen } = recordingFetch([200]);
    const client = createApiClient("http://test", { fetch, getToken: async () => "tok" });
    await sessionsGet({ client, path: { id: "s1" } });
    expect(seen[0]?.auth).toBe("Bearer tok");
  });

  it("on 401 refreshes once and replays the request, body intact, with the fresh token", async () => {
    const { fetch, seen } = recordingFetch([401, 200]);
    const onUnauthorized = vi.fn(async () => "fresh");
    const client = createApiClient("http://test", {
      fetch,
      getToken: async () => "stale",
      onUnauthorized,
    });

    const { data, response } = await sessionsSearch({ client, body: { limit: 5 } });

    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    expect(seen).toEqual([
      { auth: "Bearer stale", body: '{"limit":5}' },
      { auth: "Bearer fresh", body: '{"limit":5}' },
    ]);
    expect(response?.status).toBe(200);
    expect(data).toEqual({ items: [], total: 0 });
  });

  it("returns the 401 when the refresh gives up", async () => {
    const { fetch, seen } = recordingFetch([401]);
    const client = createApiClient("http://test", { fetch, onUnauthorized: async () => null });
    const { response } = await sessionsGet({ client, path: { id: "s1" } });
    expect(response?.status).toBe(401);
    expect(seen).toHaveLength(1);
  });

  it("asks for the current token on every event-stream (re)connect", async () => {
    const seen: (string | null)[] = [];
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      seen.push(new Request(input, init).headers.get("Authorization"));
      return new Response(null, { status: 503 }); // fail, so the stream reconnects
    });
    const tokens = ["first", "second"];
    const client = createApiClient("http://test", {
      fetch,
      getToken: async () => tokens.shift() ?? "later",
    });

    const { stream } = await eventsStream({
      client,
      sseMaxRetryAttempts: 2,
      sseDefaultRetryDelay: 1,
    });
    for await (const _ of stream) {
      // the stream only fails here
    }

    expect(seen).toEqual(["Bearer first", "Bearer second"]);
  });
});
