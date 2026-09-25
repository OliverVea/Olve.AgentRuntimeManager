import { messagesCreate, messagesList } from "@arm/client";
import { describe, expect, it, vi } from "vitest";
import { createApiClient } from "./api-client.js";

/** A fetch that records each request's Authorization header + body, answering from `statuses`. */
function recordingFetch(statuses: number[]) {
  const seen: { auth: string | null; body: string }[] = [];
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    seen.push({ auth: request.headers.get("Authorization"), body: await request.text() });
    const status = statuses[seen.length - 1] ?? 200;
    return status === 200 ? Response.json({ id: "1", text: "hi" }) : new Response(null, { status });
  });
  return { fetch, seen };
}

describe("createApiClient", () => {
  it("sends no Authorization header without a token", async () => {
    const { fetch, seen } = recordingFetch([200]);
    const client = createApiClient("http://test", { fetch, getToken: async () => null });
    await messagesList({ client });
    expect(seen[0]?.auth).toBeNull();
  });

  it("attaches the current bearer token", async () => {
    const { fetch, seen } = recordingFetch([200]);
    const client = createApiClient("http://test", { fetch, getToken: async () => "tok" });
    await messagesList({ client });
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

    const { data, response } = await messagesCreate({ client, body: { text: "hi" } });

    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    expect(seen).toEqual([
      { auth: "Bearer stale", body: '{"text":"hi"}' },
      { auth: "Bearer fresh", body: '{"text":"hi"}' },
    ]);
    expect(response?.status).toBe(200);
    expect(data).toEqual({ id: "1", text: "hi" });
  });

  it("returns the 401 when the refresh gives up", async () => {
    const { fetch, seen } = recordingFetch([401]);
    const client = createApiClient("http://test", { fetch, onUnauthorized: async () => null });
    const { response } = await messagesList({ client });
    expect(response?.status).toBe(401);
    expect(seen).toHaveLength(1);
  });
});
