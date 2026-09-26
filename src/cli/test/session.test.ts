import { describe, expect, test } from "bun:test";
import { localDateTime } from "../src/output";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";
const id = "3f2c1a9e-8d4b-4c7a-9e21-5b6d7f8a9c0d";
const createdAt = "2026-09-01T12:03:04Z";
const session = {
  id,
  status: "working",
  prompt: "fix the flaky test",
  provider: "claude",
  caller: "oribot",
  tags: { team: "infra" },
  env: { A: "1" },
  timeoutSeconds: 600,
  tools: ["arm-approved-bash"],
  skills: [],
  messaging: true,
  headless: false,
  createdAt,
};
const queued = { ...session, status: "queued", queuePosition: 3 };
const page = {
  items: [session, { ...session, id: "0b1c", status: "completed", caller: undefined, prompt: "second\nline" }],
  total: 5,
  limit: 2,
  offset: 0,
};
const errorBody = (code: string, message: string, problems?: Array<{ code: string; message: string }>) => ({
  error: { code, message, details: problems ? { problems } : {} },
});

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

describe("session create", () => {
  test("201: POSTs the body with the bearer; prints 'Started' and the session", async () => {
    const f = fakeFetch(() => ({ status: 201, body: session }));
    const r = await cli(["session", "create", "-p", "fix the flaky test"], f);
    expect(r.code).toBe(0);
    expect(r.stderr).toBe("");
    const req = f.requests[0]!;
    expect(req.method).toBe("POST");
    expect(req.url).toBe(`${url}/api/sessions`);
    expect(req.headers.get("authorization")).toBe("Bearer tok");
    expect(req.headers.get("content-type")).toContain("application/json");
    expect(req.headers.get("idempotency-key")).toBe(null);
    // Unset options are left out, so the server applies its defaults.
    expect(req.body).toEqual({ prompt: "fix the flaky test" });
    const lines = r.stdout.split("\n");
    expect(lines[0]).toBe(`Started session ${id}.`);
    expect(r.stdout).toContain(`id              ${id}`);
    expect(r.stdout).toContain("tags            team:infra");
    expect(r.stdout).toContain("env             A=1");
    expect(r.stdout).toContain("tools           arm-approved-bash");
    expect(r.stdout).not.toContain("skills");
  });

  test("202: prints 'Queued' with the queue position", async () => {
    const f = fakeFetch(() => ({ status: 202, body: queued }));
    const r = await cli(["session", "create", "--prompt", "x"], f);
    expect(r.code).toBe(0);
    expect(r.stdout.split("\n")[0]).toBe(`Queued session ${id} at position 3.`);
    expect(r.stdout).toContain("queued (position 3)");
  });

  test("--json prints the raw session for both 201 and 202", async () => {
    const f = fakeFetch(() => ({ status: 202, body: queued }));
    const r = await cli(["session", "create", "-p", "x", "--json"], f);
    expect(r.code).toBe(0);
    expect(JSON.parse(r.stdout)).toEqual(queued);
  });

  test("every option maps to the create body; repeatable options accumulate", async () => {
    const f = fakeFetch(() => ({ status: 201, body: session }));
    const r = await cli(
      [
        "session", "create",
        "-p", "do it",
        "--provider", "claude",
        "--model", "sonnet",
        "--effort", "high",
        "--system-prompt", "be brief",
        "--caller", "ci",
        "--tag", "team:infra",
        "--tag", "url:http://x",
        "--timeout-seconds", "600",
        "--headless",
        "--no-messaging",
        "--env", "A=1",
        "--env", "B=x=y",
        "--secret-env", "TOKEN=s3cret",
        "--policy", "permissive",
        "--tools", "arm-approved-bash, arm-file-ops",
        "--skills", "git",
        "--idempotency-key", "k-1",
      ],
      f,
    );
    expect(r.code).toBe(0);
    const req = f.requests[0]!;
    expect(req.headers.get("idempotency-key")).toBe("k-1");
    expect(req.body).toEqual({
      prompt: "do it",
      provider: "claude",
      model: "sonnet",
      effort: "high",
      systemPrompt: "be brief",
      caller: "ci",
      tags: { team: "infra", url: "http://x" },
      env: { A: "1", B: "x=y" },
      secretEnv: { TOKEN: "s3cret" },
      timeoutSeconds: 600,
      approvalPolicy: "permissive",
      tools: ["arm-approved-bash", "arm-file-ops"],
      skills: ["git"],
      messaging: false,
      headless: true,
    });
  });

  test("--messaging sends messaging: true", async () => {
    const f = fakeFetch(() => ({ status: 201, body: session }));
    await cli(["session", "create", "-p", "x", "--messaging"], f);
    expect(f.requests[0]!.body).toEqual({ prompt: "x", messaging: true });
  });

  test("a prompt starting with a dash uses the --prompt=… form", async () => {
    const f = fakeFetch(() => ({ status: 201, body: session }));
    const r = await cli(["session", "create", "--prompt=-5 degrees"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.body).toEqual({ prompt: "-5 degrees" });
  });

  test("503 (queue full) exits 5 with the envelope's code and message", async () => {
    const f = fakeFetch(() => ({ status: 503, body: errorBody("QUEUE_FULL", "The session queue is full.") }));
    const r = await cli(["session", "create", "-p", "x"], f);
    expect(r.code).toBe(5);
    expect(r.stdout).toBe("");
    expect(r.stderr).toBe("error: 503 Service Unavailable: QUEUE_FULL: The session queue is full.");
  });

  test("400 with several problems lists each one; exit 1", async () => {
    const body = errorBody("INVALID_REQUEST", "The request is invalid.", [
      { code: "PROMPT_REQUIRED", message: "prompt is required" },
      { code: "TIMEOUT_TOO_SMALL", message: "timeoutSeconds must be at least 1" },
    ]);
    const f = fakeFetch(() => ({ status: 400, body }));
    const r = await cli(["session", "create", "-p", "x"], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe(
      [
        "error: 400 Bad Request: INVALID_REQUEST: The request is invalid.",
        "  - PROMPT_REQUIRED: prompt is required",
        "  - TIMEOUT_TOO_SMALL: timeoutSeconds must be at least 1",
      ].join("\n"),
    );
    const j = await cli(["session", "create", "-p", "x", "--json"], f);
    expect(JSON.parse(j.stderr)).toEqual(body);
  });

  test("a single problem is not repeated under the message", async () => {
    const body = errorBody("INVALID_REQUEST", "prompt is required", [{ code: "PROMPT_REQUIRED", message: "prompt is required" }]);
    const f = fakeFetch(() => ({ status: 400, body }));
    const r = await cli(["session", "create", "-p", "x"], f);
    expect(r.stderr).toBe("error: 400 Bad Request: INVALID_REQUEST: prompt is required");
  });

  test("401 with an empty body: status line, and an HTTP_401 envelope with --json", async () => {
    const f = fakeFetch(() => ({ status: 401 }));
    const r = await cli(["session", "create", "-p", "x"], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe("error: 401 Unauthorized");
    const j = await cli(["session", "create", "-p", "x", "--json"], f);
    expect(j.code).toBe(1);
    expect(JSON.parse(j.stderr)).toEqual({
      error: { code: "HTTP_401", message: "401 Unauthorized", details: { status: 401 } },
    });
  });
});

describe("session list", () => {
  test("POSTs the filters to /search; prints a table and a paging footer", async () => {
    const f = fakeFetch(() => ({ body: page }));
    const r = await cli(
      [
        "session", "list",
        "--status", "working",
        "--caller", "oribot",
        "--tag", "team:infra",
        "--tag", "env:",
        "--after", "2026-09-01",
        "--before", "2026-09-02T10:00:00+02:00",
        "--text", "flaky",
        "--limit", "2",
        "--offset", "0",
      ],
      f,
    );
    expect(r.code).toBe(0);
    const req = f.requests[0]!;
    expect(req.method).toBe("POST");
    expect(req.url).toBe(`${url}/api/sessions/search`);
    expect(req.body).toEqual({
      status: "working",
      caller: "oribot",
      tags: { team: "infra", env: "" },
      createdAfter: "2026-09-01T00:00:00.000Z",
      createdBefore: "2026-09-02T08:00:00.000Z",
      text: "flaky",
      limit: 2,
      offset: 0,
    });
    const created = localDateTime(createdAt);
    expect(r.stdout).toBe(
      [
        `ID                                    STATUS     PROVIDER  CALLER  CREATED${" ".repeat(created.length - 7)}  PROMPT`,
        `${id}  working    claude    oribot  ${created}  fix the flaky test`,
        `0b1c                                  completed  claude            ${created}  second line`,
        "",
        "Showing 1–2 of 5 sessions · next: --offset 2",
      ].join("\n"),
    );
  });

  test("no filters sends an empty search; an empty result says so", async () => {
    const f = fakeFetch(() => ({ body: { items: [], total: 0, limit: 20, offset: 0 } }));
    const r = await cli(["session", "list"], f);
    expect(f.requests[0]!.body).toEqual({});
    expect(r.stdout).toBe("No sessions.");
  });

  test("the last page has no next hint; long prompts are truncated", async () => {
    const long = "a".repeat(80);
    const f = fakeFetch(() => ({ body: { items: [{ ...session, prompt: long }], total: 3, limit: 2, offset: 2 } }));
    const r = await cli(["session", "list", "--offset", "2", "--limit", "2"], f);
    expect(r.stdout).toContain(`${"a".repeat(59)}…`);
    expect(r.stdout.split("\n").at(-1)).toBe("Showing 3–3 of 3 sessions");
  });

  test("--json prints the raw page", async () => {
    const f = fakeFetch(() => ({ body: page }));
    const r = await cli(["session", "list", "--json"], f);
    expect(JSON.parse(r.stdout)).toEqual(page);
  });
});

describe("session get", () => {
  test("GETs by id and prints key/value", async () => {
    const f = fakeFetch(() => ({ body: session }));
    const r = await cli(["session", "get", id], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/sessions/${id}`);
    expect(r.stdout.split("\n")[0]).toBe(`id              ${id}`);
    expect(r.stdout).toContain("status          working");
  });

  test("escapes the id in the path", async () => {
    const f = fakeFetch(() => ({ body: session }));
    await cli(["session", "get", "a/b c"], f);
    expect(f.requests[0]!.url).toBe(`${url}/api/sessions/a%2Fb%20c`);
  });

  test("404 exits 3; --json prints the envelope on stderr", async () => {
    const body = errorBody("SESSION_NOT_FOUND", "Session not found.");
    const f = fakeFetch(() => ({ status: 404, body }));
    const r = await cli(["session", "get", id], f);
    expect(r.code).toBe(3);
    expect(r.stdout).toBe("");
    expect(r.stderr).toBe("error: 404 Not Found: SESSION_NOT_FOUND: Session not found.");
    const j = await cli(["session", "get", id, "--json"], f);
    expect(j.code).toBe(3);
    expect(JSON.parse(j.stderr)).toEqual(body);
  });
});

describe("session kill", () => {
  test("POSTs {reason} to /kill", async () => {
    const killed = { ...session, status: "killed", killReason: "stuck", killSource: "user" };
    const f = fakeFetch(() => ({ body: killed }));
    const r = await cli(["session", "kill", id, "--reason", "stuck"], f);
    expect(r.code).toBe(0);
    const req = f.requests[0]!;
    expect(req.method).toBe("POST");
    expect(req.url).toBe(`${url}/api/sessions/${id}/kill`);
    expect(req.body).toEqual({ reason: "stuck" });
    expect(r.stdout.split("\n")[0]).toBe(`Killed session ${id}.`);
    expect(r.stdout).toContain("killSource      user");
  });

  test("without --reason it still sends a JSON body, {}", async () => {
    const f = fakeFetch(() => ({ body: { ...session, status: "killed" } }));
    await cli(["session", "kill", id], f);
    expect(f.requests[0]!.body).toEqual({});
    expect(f.requests[0]!.headers.get("content-type")).toContain("application/json");
  });

  test("409 (already ended) exits 4", async () => {
    const f = fakeFetch(() => ({ status: 409, body: errorBody("SESSION_ALREADY_ENDED", "The session has already ended.") }));
    const r = await cli(["session", "kill", id], f);
    expect(r.code).toBe(4);
    expect(r.stderr).toBe("error: 409 Conflict: SESSION_ALREADY_ENDED: The session has already ended.");
  });
});

describe("session delete", () => {
  test("DELETEs the id; 204", async () => {
    const f = fakeFetch(() => ({ status: 204 }));
    const r = await cli(["session", "delete", id], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("DELETE");
    expect(f.requests[0]!.url).toBe(`${url}/api/sessions/${id}`);
    expect(r.stdout).toBe(`Deleted session ${id}.`);
    const j = await cli(["session", "delete", id, "--json"], f);
    expect(JSON.parse(j.stdout)).toEqual({});
  });

  test("409 (not ended) exits 4", async () => {
    const f = fakeFetch(() => ({ status: 409, body: errorBody("SESSION_NOT_ENDED", "Kill the session first.") }));
    const r = await cli(["session", "delete", id], f);
    expect(r.code).toBe(4);
  });
});

describe("session usage errors exit 2 without a request", () => {
  const cases: Array<[string, string[], string]> = [
    ["missing prompt", ["session", "create"], "missing required option --prompt"],
    ["empty prompt", ["session", "create", "-p", ""], "--prompt must not be empty"],
    ["tag without a colon", ["session", "create", "-p", "x", "--tag", "team"], "--tag must be key:value, got 'team'"],
    ["tag with an empty key", ["session", "list", "--tag", ":x"], "--tag must be key:value"],
    ["duplicate tag", ["session", "create", "-p", "x", "--tag", "a:1", "--tag", "a:2"], "--tag 'a' is given more than once"],
    ["env without =", ["session", "create", "-p", "x", "--env", "A"], "--env must be KEY=VALUE, got 'A'"],
    ["secret-env without =", ["session", "create", "-p", "x", "--secret-env", "=v"], "--secret-env must be KEY=VALUE"],
    ["zero timeout", ["session", "create", "-p", "x", "--timeout-seconds", "0"], "--timeout-seconds must be a positive integer"],
    ["both messaging flags", ["session", "create", "-p", "x", "--messaging", "--no-messaging"], "mutually exclusive"],
    ["unknown status", ["session", "list", "--status", "running"], "--status must be one of queued, working, waiting, completed, killed, failed"],
    ["limit above 100", ["session", "list", "--limit", "101"], "--limit must be an integer from 1 to 100"],
    ["negative offset", ["session", "list", "--offset", "-1"], "--offset"],
    ["bad date", ["session", "list", "--after", "yesterday"], "--after must be a date or date-time"],
    ["missing id", ["session", "kill"], "missing argument <id>"],
    ["option of another command", ["session", "get", id, "--limit", "2"], "--limit"],
  ];
  for (const [name, argv, message] of cases) {
    test(name, async () => {
      const f = fakeFetch(() => ({ body: session }));
      const r = await cli(argv, f);
      expect(r.code).toBe(2);
      expect(r.stdout).toBe("");
      expect(r.stderr).toContain(message);
      expect(f.requests).toHaveLength(0);
    });
  }
});

describe("network failures", () => {
  test("a fetch that throws exits 1 with the URL in the message", async () => {
    const f = fakeFetch(() => new TypeError("fetch failed"));
    const r = await cli(["session", "get", id], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe(`error: could not reach ${url}/api/sessions/${id}: fetch failed`);
  });

  test("with --json it is a NETWORK_ERROR envelope", async () => {
    const f = fakeFetch(() => new TypeError("fetch failed"));
    const r = await cli(["session", "list", "--json"], f);
    expect(r.code).toBe(1);
    expect((JSON.parse(r.stderr) as { error: { code: string } }).error.code).toBe("NETWORK_ERROR");
  });
});
