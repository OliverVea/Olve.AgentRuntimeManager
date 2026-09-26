import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

describe("server info", () => {
  test("GETs the server info and prints key/value", async () => {
    const f = fakeFetch(() => ({ body: { version: "2026.9.26.8", environment: "beta" } }));
    const r = await cli(["server", "info"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/server-info`);
    expect(f.requests[0]!.headers.get("authorization")).toBe("Bearer tok");
    expect(r.stdout).toBe("version      2026.9.26.8\nenvironment  beta");
  });

  test("is the default command of `arm server`; a local run prints empty values", async () => {
    const f = fakeFetch(() => ({ body: { version: null, environment: null } }));
    const r = await cli(["server"], f);
    expect(r.code).toBe(0);
    expect(r.stdout).toBe("version\nenvironment");
  });

  test("--json prints the payload", async () => {
    const f = fakeFetch(() => ({ body: { version: "2026.9.26.8", environment: "beta" } }));
    const r = await cli(["server", "info", "--json"], f);
    expect(JSON.parse(r.stdout)).toEqual({ version: "2026.9.26.8", environment: "beta" });
  });
});
