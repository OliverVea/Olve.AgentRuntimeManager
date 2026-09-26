import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

const health = [
  {
    provider: "claude",
    status: "limited",
    reason: "You've hit your session limit · resets 10pm (UTC)",
    since: "2026-09-26T18:00:00Z",
    until: "2026-09-26T22:00:00Z",
  },
  { provider: "fake", status: "available" },
];

describe("provider health", () => {
  test("GETs every provider's health and prints a table", async () => {
    const f = fakeFetch(() => ({ body: health }));
    const r = await cli(["provider", "health"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/providers/health`);
    const lines = r.stdout.split("\n");
    expect(lines[0]).toMatch(/^PROVIDER\s+STATUS\s+SINCE\s+UNTIL\s+REASON$/);
    expect(lines[1]).toMatch(/^claude\s+limited\s+\d{4}-\d\d-\d\d \d\d:\d\d\s+\d{4}-\d\d-\d\d \d\d:\d\d\s+You've hit your session limit/);
    expect(lines[2]).toBe("fake      available");
  });

  test("is the default command of `arm provider`", async () => {
    const f = fakeFetch(() => ({ body: health }));
    const r = await cli(["provider"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.url).toBe(`${url}/api/providers/health`);
  });

  test("--json prints the payload", async () => {
    const f = fakeFetch(() => ({ body: health }));
    const r = await cli(["provider", "health", "--json"], f);
    expect(JSON.parse(r.stdout)).toEqual(health);
  });
});
