import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";
const id = "11111111-1111-4111-8111-111111111111";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

const activity = {
  lines: [
    { kind: "text", text: "Checking the build." },
    { kind: "tool", text: "Bash → npx mise run ci" },
    { kind: "error", text: "fatal: denied" },
  ],
};

describe("session activity", () => {
  test("GETs the latest lines and prints one per line, marked by kind", async () => {
    const f = fakeFetch(() => ({ body: activity }));
    const r = await cli(["session", "activity", id], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/sessions/${id}/activity`);
    expect(r.stdout).toBe("› Checking the build.\n⚙ Bash → npx mise run ci\n! fatal: denied");
  });

  test("--limit asks for that many", async () => {
    const f = fakeFetch(() => ({ body: activity }));
    const r = await cli(["session", "activity", id, "--limit", "50"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.url).toBe(`${url}/api/sessions/${id}/activity?limit=50`);
  });

  test("--json prints the response as it came", async () => {
    const f = fakeFetch(() => ({ body: activity }));
    const r = await cli(["session", "activity", id, "--json"], f);
    expect(JSON.parse(r.stdout)).toEqual(activity);
  });

  test("no lines yet", async () => {
    const f = fakeFetch(() => ({ body: { lines: [] } }));
    const r = await cli(["session", "activity", id], f);
    expect(r.stdout).toBe("No activity yet.");
  });

  test("an unknown session exits 3", async () => {
    const body = { error: { code: "SESSION_NOT_FOUND", message: "Session not found.", details: {} } };
    const f = fakeFetch(() => ({ status: 404, body }));
    const r = await cli(["session", "activity", id], f);
    expect(r.code).toBe(3);
    expect(r.stderr).toBe("error: 404 Not Found: SESSION_NOT_FOUND: Session not found.");
    const j = await cli(["session", "activity", id, "--json"], fakeFetch(() => ({ status: 404, body })));
    expect(j.code).toBe(3);
    expect(JSON.parse(j.stderr)).toEqual(body);
  });

  test("a 400 from the server is shown", async () => {
    const body = { error: { code: "INVALID_REQUEST", message: "'limit' cannot exceed 50.", details: {} } };
    const f = fakeFetch(() => ({ status: 400, body }));
    const r = await cli(["session", "activity", id], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe("error: 400 Bad Request: INVALID_REQUEST: 'limit' cannot exceed 50.");
  });

  const usage: Array<[string, string[], string]> = [
    ["missing id", ["session", "activity"], "missing argument <id>"],
    ["limit 0", ["session", "activity", id, "--limit", "0"], "--limit must be an integer from 1 to 50, got '0'"],
    ["limit 51", ["session", "activity", id, "--limit", "51"], "--limit must be an integer from 1 to 50, got '51'"],
    ["limit not a number", ["session", "activity", id, "--limit", "few"], "--limit must be an integer from 1 to 50"],
  ];
  for (const [name, argv, message] of usage) {
    test(`usage: ${name} exits 2 without a request`, async () => {
      const f = fakeFetch(() => ({ body: activity }));
      const r = await cli(argv, f);
      expect(r.code).toBe(2);
      expect(r.stdout).toBe("");
      expect(r.stderr).toContain(message);
      expect(f.requests).toHaveLength(0);
    });
  }
});
