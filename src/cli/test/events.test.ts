import { describe, expect, test } from "bun:test";
import { formatEvent, tailSettings } from "../src/commands/events";
import { frame, runCli, type SseReply, sseFetch } from "./helpers";

const url = "http://arm.test";
const at = "2026-09-01T12:03:04Z";
const sessionId = "3f2c1a9e-8d4b-4c7a-9e21-5b6d7f8a9c0d";
const session = {
  id: sessionId,
  status: "queued",
  prompt: "say \"hello\"",
  provider: "claude",
  tags: {},
  env: {},
  timeoutSeconds: 600,
  tools: [],
  skills: [],
  messaging: true,
  headless: false,
  createdAt: at,
};

const heartbeat = frame("heartbeat", { type: "heartbeat", at });
const created = frame("session.created", { type: "session.created", at, sessionId, session }, "101");
const started = frame(
  "session.started",
  { type: "session.started", at, sessionId, previous: "queued", providerSessionId: "p-1" },
  "102",
);
const killed = frame(
  "session.killed",
  { type: "session.killed", at, sessionId, previous: "working", reason: "stuck", source: "user" },
  "103",
);

/** Local wall-clock time of `at`, as the pretty output prints it. */
function clock(iso: string): string {
  const d = new Date(iso);
  return [d.getHours(), d.getMinutes(), d.getSeconds()].map((n) => String(n).padStart(2, "0")).join(":");
}

/** Runs `arm events …` against scripted replies; Ctrl+C (abort) once `lines` stdout lines arrived. */
async function tail(argv: string[], replies: SseReply[], lines: number) {
  const f = sseFetch(replies);
  const interrupt = new AbortController();
  const result = await runCli(["events", ...argv, "--url", url, "--token", "tok"], {
    fetch: f.fetch,
    signal: interrupt.signal,
    onStdout: (out) => {
      if (out.length >= lines) interrupt.abort();
    },
  });
  return { ...result, requests: f.requests };
}

describe("arm events", () => {
  test("pretty: one line per event, heartbeats hidden; Ctrl+C exits 0", async () => {
    const r = await tail([], [{ frames: [heartbeat, created, heartbeat, started, killed], then: "hang" }], 3);
    expect(r.code).toBe(0);
    expect(r.stderr).toBe("");
    const time = clock(at);
    expect(r.stdout).toBe(
      [
        `${time} session.created ${sessionId} "say \\"hello\\""`,
        `${time} session.started ${sessionId} providerSessionId=p-1`,
        `${time} session.killed ${sessionId} source=user "stuck"`,
      ].join("\n"),
    );
  });

  test("Ctrl+C cancels the stream without aborting the connected fetch underneath it", async () => {
    const r = await tail([], [{ frames: [created], then: "hang" }], 1);
    expect(r.code).toBe(0);
    // Aborting the fetch would error the body under the SSE client's own cancel() (Bun: an
    // unhandled AbortError and exit 1); the client cancels the body instead.
    expect(r.requests[0]!.signal.aborted).toBe(false);
  });

  test("Ctrl+C while connecting aborts the request and exits 0", async () => {
    const f = sseFetch(["pending"]);
    const interrupt = new AbortController();
    setTimeout(() => interrupt.abort(), 20);
    const r = await runCli(["events", "--url", url], { fetch: f.fetch, signal: interrupt.signal });
    expect(r.code).toBe(0);
    expect(r.stderr).toBe("");
    expect(f.requests).toHaveLength(1);
    expect(f.requests[0]!.signal.aborted).toBe(true);
  });

  test("sends GET /api/events with the bearer, an SSE Accept and comma-list filters", async () => {
    const r = await tail(
      ["--event", "session.created,session.killed", "--exclude-event", "session.started"],
      [{ frames: [created], then: "hang" }],
      1,
    );
    expect(r.code).toBe(0);
    expect(r.requests).toHaveLength(1);
    const req = r.requests[0]!;
    const sent = new URL(req.url);
    expect(sent.pathname).toBe("/api/events");
    expect(sent.searchParams.get("event")).toBe("session.created,session.killed");
    expect(sent.searchParams.get("exclude_event")).toBe("session.started");
    expect(req.headers.get("authorization")).toBe("Bearer tok");
    expect(req.headers.get("accept")).toBe("text/event-stream");
    expect(req.headers.get("last-event-id")).toBe(null);
  });

  test("--last-event-id is sent as Last-Event-ID", async () => {
    const r = await tail(["--last-event-id", "100"], [{ frames: [created], then: "hang" }], 1);
    expect(r.requests[0]!.headers.get("last-event-id")).toBe("100");
  });

  test("--json: NDJSON, one event per line with its id; heartbeats included without one", async () => {
    const r = await tail(["--json"], [{ frames: [heartbeat, created, heartbeat], then: "hang" }], 3);
    expect(r.code).toBe(0);
    const lines = r.stdout.split("\n").map((l) => JSON.parse(l));
    expect(lines).toEqual([
      { event: "heartbeat", data: { type: "heartbeat", at } },
      { event: "session.created", id: "101", data: { type: "session.created", at, sessionId, session } },
      { event: "heartbeat", data: { type: "heartbeat", at } },
    ]);
  });

  test("reconnects with the last event id when the server ends the stream", async () => {
    const saved = tailSettings.reconnectDelayMs;
    tailSettings.reconnectDelayMs = 0;
    try {
      const r = await tail(
        ["--json"],
        [
          { frames: [heartbeat, created, heartbeat], then: "close" },
          { frames: [killed], then: "hang" },
        ],
        4,
      );
      expect(r.code).toBe(0);
      expect(r.requests).toHaveLength(2);
      expect(r.requests[1]!.headers.get("last-event-id")).toBe("101");
      expect(JSON.parse(r.stdout.split("\n")[3]!).id).toBe("103");
      expect(r.stderr).toContain("the server ended the stream; reconnecting");
    } finally {
      tailSettings.reconnectDelayMs = saved;
    }
  });

  test("after connecting, a 5xx (the edge during a redeploy) is retried, not fatal", async () => {
    const saved = { ...tailSettings };
    tailSettings.reconnectDelayMs = 0;
    tailSettings.retryDelayMs = 0;
    try {
      const r = await tail(
        ["--json"],
        [
          { frames: [created], then: "close" },
          { status: 502 },
          { frames: [killed], then: "hang" },
        ],
        2,
      );
      expect(r.code).toBe(0);
      expect(r.requests).toHaveLength(3);
      expect(r.requests[2]!.headers.get("last-event-id")).toBe("101");
      expect(JSON.parse(r.stdout.split("\n")[1]!).id).toBe("103");
      expect(r.stderr).toContain("warning: connection lost (SSE failed: 502");
    } finally {
      Object.assign(tailSettings, saved);
    }
  });

  test("a 5xx on the first connection is fatal (503: exit 5)", async () => {
    const r = await tail([], [{ status: 503 }], 1);
    expect(r.code).toBe(5);
    expect(r.requests).toHaveLength(1);
    expect(r.stderr).toBe("error: 503 Service Unavailable");
  });

  test("a 401 after connecting (expired token) stops the tail with exit 1", async () => {
    const saved = { ...tailSettings };
    tailSettings.reconnectDelayMs = 0;
    try {
      const r = await tail([], [{ frames: [created], then: "close" }, { status: 401 }], 99);
      expect(r.code).toBe(1);
      expect(r.requests).toHaveLength(2);
      expect(r.stderr).toContain("error: 401 Unauthorized");
    } finally {
      Object.assign(tailSettings, saved);
    }
  });

  test("a 400 (unknown filter) fails once with the API's message, exit 1", async () => {
    const body = { error: { code: "INVALID_REQUEST", message: "'event' has unknown event 'nope'.", details: {} } };
    const r = await tail(["--event", "nope"], [{ status: 400, body }], 1);
    expect(r.code).toBe(1);
    expect(r.requests).toHaveLength(1);
    expect(r.stdout).toBe("");
    expect(r.stderr).toBe("error: 400 Bad Request: INVALID_REQUEST: 'event' has unknown event 'nope'.");
  });

  test("a 401 fails with exit 1; --json prints the error envelope", async () => {
    const r = await tail(["--json"], [{ status: 401 }], 1);
    expect(r.code).toBe(1);
    expect(JSON.parse(r.stderr).error.code).toBe("HTTP_401");
  });

  test("an unreachable server fails with exit 1 instead of retrying forever", async () => {
    const r = await tail([], [new TypeError("Unable to connect")], 1);
    expect(r.code).toBe(1);
    expect(r.requests).toHaveLength(1);
    expect(r.stderr).toBe(`error: could not reach ${url}/api/events: Unable to connect`);
  });

  test("--help shows the events options", async () => {
    const r = await runCli(["events", "--help"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toContain("arm events [options]");
    for (const option of ["--event", "--exclude-event", "--last-event-id"]) expect(r.stdout).toContain(option);
  });
});

describe("formatEvent", () => {
  const line = (data: object) => formatEvent({ event: "x", id: "1", data: { at, sessionId, ...data } as never });

  test("one line per session event", () => {
    const time = clock(at);
    expect(line({ type: "session.queued", position: 3 })).toBe(`${time} session.queued ${sessionId} position=3`);
    expect(line({ type: "session.waiting", previous: "working" })).toBe(`${time} session.waiting ${sessionId}`);
    expect(line({ type: "session.resumed", previous: "waiting" })).toBe(`${time} session.resumed ${sessionId}`);
    expect(line({ type: "session.completed", previous: "working", exitCode: 0, summary: "done" })).toBe(
      `${time} session.completed ${sessionId} exitCode=0 "done"`,
    );
    expect(line({ type: "session.failed", previous: "working", error: "boom" })).toBe(
      `${time} session.failed ${sessionId} "boom"`,
    );
    expect(line({ type: "session.killed", previous: "queued", source: "timeout" })).toBe(
      `${time} session.killed ${sessionId} source=timeout`,
    );
  });

  test("an event this CLI doesn't know prints its name and remaining data", () => {
    const unknown = formatEvent({
      event: "session.revived",
      id: "1",
      data: { type: "session.revived", at, sessionId: "s1" } as never,
    });
    expect(unknown).toBe(`${clock(at)} session.revived {"sessionId":"s1"}`);
  });
});
