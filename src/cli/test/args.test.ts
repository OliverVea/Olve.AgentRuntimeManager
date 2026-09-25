import { describe, expect, test } from "bun:test";
import { DEFAULT_URL, VERSION } from "../src/cli";
import { groups } from "../src/commands";
import { allOptions } from "../src/registry";
import { fakeFetch, runCli } from "./helpers";

const page = { items: [], pageNumber: 1, pageSize: 20, totalCount: 0 };

describe("help and version", () => {
  test("--help prints root help to stdout, exit 0", async () => {
    const r = await runCli(["--help"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toContain("arm <group> <command>");
    expect(r.stdout).toContain("message");
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
    const r = await runCli(["message", "--help"]);
    expect(r.code).toBe(0);
    for (const verb of ["list", "get", "create", "update", "delete"]) expect(r.stdout).toContain(verb);
  });

  test("group without a command prints group help to stderr, exit 2", async () => {
    const r = await runCli(["message"]);
    expect(r.code).toBe(2);
    expect(r.stderr).toContain("arm message list");
  });

  test("command --help shows its arguments and options, even with missing args", async () => {
    const r = await runCli(["message", "update", "--help"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toContain("arm message update <id> <text>");
    const list = await runCli(["message", "list", "-h"]);
    expect(list.stdout).toContain("--page-size N");
  });
});

describe("usage errors exit 2", () => {
  const cases: Array<[string, string[], string]> = [
    ["unknown group", ["nope"], "unknown command group 'nope'"],
    ["unknown command", ["message", "frob"], "unknown command 'message frob'"],
    ["unknown option", ["message", "list", "--bogus"], "--bogus"],
    ["option of another command", ["message", "get", "x", "--page", "2"], "--page"],
    ["missing argument", ["message", "get"], "missing argument <id>"],
    ["missing second argument", ["message", "update", "id"], "missing argument <text>"],
    ["extra argument", ["message", "create", "a", "b"], "unexpected argument 'b'"],
    ["non-numeric page", ["message", "list", "--page", "two"], "--page must be a positive integer"],
    ["zero page size", ["message", "list", "--page-size", "0"], "--page-size must be a positive integer"],
    ["json and pretty", ["message", "list", "--json", "--pretty"], "mutually exclusive"],
    ["invalid url", ["message", "list", "--url", "ftp://x"], "invalid URL"],
    ["missing option value", ["message", "list", "--url"], "--url"],
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
    const r = await runCli(["message", "get", "--json"]);
    expect(r.code).toBe(2);
    expect(JSON.parse(r.stderr)).toEqual({
      error: { code: "USAGE", message: "missing argument <id> (expected <id>)", details: null },
    });
  });
});

describe("option parsing", () => {
  test("global options may come before the group, in --opt=value form", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    const r = await runCli(["--url=http://a.test:1", "--json", "message", "list"], { fetch });
    expect(r.code).toBe(0);
    expect(requests[0]!.url).toBe("http://a.test:1/api/messages");
  });

  test("an option value is never taken for the group or command", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    const r = await runCli(["--token", "message", "message", "list"], { fetch });
    expect(r.code).toBe(0);
    expect(requests[0]!.headers.get("authorization")).toBe("Bearer message");
  });

  test("'--' lets text start with a dash", async () => {
    const { fetch, requests } = fakeFetch((req) => ({ body: { id: "1", ...(req.body as object) } }));
    const r = await runCli(["message", "create", "--", "-5 degrees"], { fetch });
    expect(r.code).toBe(0);
    expect(requests[0]!.body).toEqual({ text: "-5 degrees" });
  });

  test("URL precedence: --url over ARM_URL over the default", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    await runCli(["message", "list"], { fetch });
    await runCli(["message", "list"], { fetch, env: { ARM_URL: "http://env.test/" } });
    await runCli(["message", "list", "--url", "http://flag.test"], { fetch, env: { ARM_URL: "http://env.test" } });
    expect(requests.map((r) => r.url)).toEqual([
      `${DEFAULT_URL}/api/messages`,
      "http://env.test/api/messages",
      "http://flag.test/api/messages",
    ]);
  });

  test("token precedence: --token over ARM_TOKEN; none means no Authorization header", async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: page }));
    await runCli(["message", "list"], { fetch });
    await runCli(["message", "list"], { fetch, env: { ARM_TOKEN: "env-tok" } });
    await runCli(["message", "list", "--token", "flag-tok"], { fetch, env: { ARM_TOKEN: "env-tok" } });
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
