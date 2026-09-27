import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

const variable = { name: "GIT_AUTHOR_NAME", value: "Oliver Vea", default: true, updatedAt: "2026-09-27T10:00:00Z" };

describe("env", () => {
  test("list GETs the registered variables and prints a table (the default command)", async () => {
    const f = fakeFetch(() => ({ body: [variable] }));
    const r = await cli(["env"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.url).toBe(`${url}/api/env`);
    const lines = r.stdout.split("\n");
    expect(lines[0]).toMatch(/^NAME\s+VALUE\s+DEFAULT\s+UPDATED$/);
    expect(lines[1]).toMatch(/^GIT_AUTHOR_NAME\s+Oliver Vea\s+yes\s+\d{4}-\d\d-\d\d \d\d:\d\d$/);
  });

  test("set PUTs the value, not default unless --default", async () => {
    const f = fakeFetch(() => ({ body: { ...variable, default: false } }));
    const r = await cli(["env", "set", "GIT_AUTHOR_NAME", "Oliver Vea"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("PUT");
    expect(f.requests[0]!.url).toBe(`${url}/api/env/GIT_AUTHOR_NAME`);
    expect(f.requests[0]!.body).toEqual({ value: "Oliver Vea", default: false });
    expect(r.stdout).toBe("Set GIT_AUTHOR_NAME.");
  });

  test("set --default gives it to every agent", async () => {
    const f = fakeFetch(() => ({ body: variable }));
    const r = await cli(["env", "set", "GIT_AUTHOR_NAME", "Oliver Vea", "--default"], f);
    expect(f.requests[0]!.body).toEqual({ value: "Oliver Vea", default: true });
    expect(r.stdout).toBe("Set GIT_AUTHOR_NAME (every agent gets it).");
  });

  test("delete DELETEs it", async () => {
    const f = fakeFetch(() => ({ status: 204 }));
    const r = await cli(["env", "delete", "GIT_AUTHOR_NAME"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("DELETE");
    expect(f.requests[0]!.url).toBe(`${url}/api/env/GIT_AUTHOR_NAME`);
  });
});
