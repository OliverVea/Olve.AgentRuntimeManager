import { describe, expect, test } from "bun:test";
import { DEFAULT_URL, VERSION } from "../src/cli";
import { groups } from "../src/commands";
import { allOptions } from "../src/registry";
import { fakeFetch, runCli } from "./helpers";

const page = { items: [], total: 0, limit: 20, offset: 0 };

describe("help and version", () => {
  test("--help prints root help to stdout, exit 0", async () => {
    const r = await runCli(["--help"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toContain("arm <group> <command>");
    expect(r.stdout).toContain("session");
    expect(r.stdout).toContain("4 conflict (409)");
    expect(r.stderr).toBe("");
  });

  test("-h works too", async () => {
    expect((await runCli(["-h"])).code).toBe(0);
  });

  test("no arguments prints root help to stderr, exit 2", async () => {
    const r = await runCli([]);
    expect(r.code).toBe(2);
    expect(r.stdout).toBe("");
    expect(r.stderr).toContain("Usage:");
  });

  test("--version prints the package version", async () => {
    const r = await runCli(["--version"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toBe(`arm ${VERSION}`);
  });

  test("group --help lists the group's commands", async () => {
    const r = await runCli(["session", "--help"]);
    expect(r.code).toBe(0);
    for (const verb of ["create", "list", "get", "kill", "delete"]) expect(r.stdout).toContain(verb);
  });

  test("group without a command prints group help to stderr, exit 2", async () => {
    const r = await runCli(["session"]);
    expect(r.code).toBe(2);
    expect(r.stderr).toContain("arm session list");
  });

  test("command --help shows its arguments and options, even with missing args", async () => {
    const r = await runCli(["session", "kill", "--help"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toContain("arm session kill <id> [options]");
    expect(r.stdout).toContain("--reason TEXT");
    expect(r.stdout).toContain("--caller X");
    const create = await runCli(["session", "create", "-h"]);
    expect(create.stdout).toContain("-p, --prompt TEXT");
    for (const option of ["--provider X", "--model X", "--caller X", "--timeout-seconds N", "--idempotency-key KEY"]) {
      expect(create.stdout).toContain(option);
    }
    expect(create.stdout).not.toContain("--tag");
  });
});

describe("usage errors exit 2", () => {
  const cases: Array<[string, string[], string]> = [
    ["unknown group", ["nope"], "unknown command group 'nope'"],
    ["unknown command", ["session", "frob"], "unknown command 'session frob'"],
    ["unknown option", ["session", "list", "--bogus"], "--bogus"],
    ["option of another command", ["session", "get", "x", "--offset", "2"], "--offset"],
    ["missing argument", ["session", "get"], "missing argument <id>"],
    ["extra argument", ["session", "get", "a", "b"], "unexpected argument 'b'"],
    ["non-numeric limit", ["session", "list", "--limit", "two"], "--limit must be an integer from 1 to 100"],
    ["json and pretty", ["session", "list", "--json", "--pretty"], "mutually exclusive"],
    ["invalid url", ["session", "list", "--url", "ftp://x"], "invalid URL"],
    ["missing option value", ["session", "list", "--url"], "--url"],
  ];
  for (const [name, argv, message] of cases) {
    test(name, async () => {
      const r = await runCli(argv);
      expect(r.code).toBe(2);
      expect(r.stdout).toBe("");
      expect(r.stderr).toContain(message);
    });
  }

  test("with --json the usage error is a JSON envelope", async () => {
    const r = await runCli(["session", "get", "--json"]);
    expect(r.code).toBe(2);
    expect(JSON.parse(r.stderr)).toEqual({
      error: { code: "USAGE", message: "missing argument <id> (expected <id>)", details: null },
    });
  });
});

describe("option parsing", () => {
  test("global options may come before the group, in --opt=value form", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    const r = await runCli(["--url=http://a.test:1", "--json", "session", "list"], { fetch });
    expect(r.code).toBe(0);
    expect(requests[0]!.url).toBe("http://a.test:1/api/sessions/search");
  });

  test("an option value is never taken for the group or command", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    const r = await runCli(["--token", "session", "session", "list"], { fetch });
    expect(r.code).toBe(0);
    expect(requests[0]!.headers.get("authorization")).toBe("Bearer session");
  });

  test("'--' lets an argument start with a dash", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: { id: "-1" } }));
    const r = await runCli(["session", "get", "--", "-1"], { fetch });
    expect(r.code).toBe(0);
    expect(requests[0]!.url).toBe(`${DEFAULT_URL}/api/sessions/-1`);
  });

  test("URL precedence: --url over ARM_URL over the default", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    await runCli(["session", "list"], { fetch });
    await runCli(["session", "list"], { fetch, env: { ARM_URL: "http://env.test/" } });
    await runCli(["session", "list", "--url", "http://flag.test"], { fetch, env: { ARM_URL: "http://env.test" } });
    expect(requests.map((r) => r.url)).toEqual([
      `${DEFAULT_URL}/api/sessions/search`,
      "http://env.test/api/sessions/search",
      "http://flag.test/api/sessions/search",
    ]);
  });

  test("token precedence: --token over ARM_TOKEN; none means no Authorization header", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    await runCli(["session", "list"], { fetch });
    await runCli(["session", "list"], { fetch, env: { ARM_TOKEN: "env-tok" } });
    await runCli(["session", "list", "--token", "flag-tok"], { fetch, env: { ARM_TOKEN: "env-tok" } });
    expect(requests.map((r) => r.headers.get("authorization"))).toEqual([
      null,
      "Bearer env-tok",
      "Bearer flag-tok",
    ]);
  });
});

describe("registry", () => {
  test("option declarations across commands do not conflict", () => {
    expect(() => allOptions(groups)).not.toThrow();
  });

  test("group and command names are unique", () => {
    const names = groups.map((g) => g.name);
    expect(new Set(names).size).toBe(names.length);
    for (const g of groups) {
      const verbs = g.commands.map((c) => c.name);
      expect(new Set(verbs).size).toBe(verbs.length);
    }
  });
});
