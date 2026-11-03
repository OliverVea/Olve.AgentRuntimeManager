import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";

// The options go first: `set` puts the server's own command last, after `--`.
const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli(["--url", url, "--token", "tok", ...argv], { fetch: f.fetch });

const memory = {
  name: "memory",
  description: "A knowledge graph.",
  command: "npx",
  args: ["-y", "@modelcontextprotocol/server-memory@2.0.1"],
  env: { MEMORY_FILE_PATH: "/tmp/memory.json", DEBUG: "1" },
  updatedAt: "2026-11-02T10:00:00Z",
};
const bare = { name: "local", description: "Our own.", command: "local-mcp", args: [], env: {}, updatedAt: "2026-11-02T11:00:00Z" };

describe("mcp list", () => {
  test("GETs the servers and prints one line per server (the default command)", async () => {
    const f = fakeFetch(() => ({ body: [memory, bare] }));
    const r = await cli(["mcp"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/mcp-servers`);
    expect(r.stdout.split("\n")).toEqual([
      "SERVER  COMMAND                                           DESCRIPTION",
      "memory  npx -y @modelcontextprotocol/server-memory@2.0.1  A knowledge graph.",
      "local   local-mcp                                         Our own.",
    ]);
  });

  test("says so when none are registered", async () => {
    const r = await cli(["mcp", "list"], fakeFetch(() => ({ body: [] })));
    expect(r.stdout).toBe("No MCP servers registered.");
  });
});

describe("mcp get", () => {
  test("GETs by name and prints one field per line, leaving out an empty env", async () => {
    const f = fakeFetch(() => ({ body: memory }));
    const r = await cli(["mcp", "get", "memory"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.url).toBe(`${url}/api/mcp-servers/memory`);
    expect(r.stdout.split("\n")).toEqual([
      "name         memory",
      "description  A knowledge graph.",
      "command      npx -y @modelcontextprotocol/server-memory@2.0.1",
      "env          MEMORY_FILE_PATH=/tmp/memory.json DEBUG=1",
      "updatedAt    2026-11-02T10:00:00Z",
    ]);
    const none = await cli(["mcp", "get", "local"], fakeFetch(() => ({ body: bare })));
    expect(none.stdout).not.toContain("env");
  });
});

describe("mcp set", () => {
  test("PUTs the whole definition: everything after -- is the command, its flags its own", async () => {
    const f = fakeFetch(() => ({ body: memory }));
    const r = await cli(
      ["mcp", "set", "memory", "A knowledge graph.", "--env", "MEMORY_FILE_PATH=/tmp/a=b.json", "--env", "DEBUG=1", "--", "npx", "-y", "pkg@1.2.3", "--help"],
      f,
    );
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("PUT");
    expect(f.requests[0]!.url).toBe(`${url}/api/mcp-servers/memory`);
    expect(f.requests[0]!.body).toEqual({
      description: "A knowledge graph.",
      command: "npx",
      args: ["-y", "pkg@1.2.3", "--help"],
      env: { MEMORY_FILE_PATH: "/tmp/a=b.json", DEBUG: "1" },
    });
    expect(r.stdout).toBe("Set memory. It starts when a session names it: --tool memory.");
  });

  test("sends no arguments and no env when none are given: it replaces the server whole", async () => {
    const f = fakeFetch(() => ({ body: bare }));
    await cli(["mcp", "set", "local", "Our own.", "local-mcp"], f);
    expect(f.requests[0]!.body).toEqual({ description: "Our own.", command: "local-mcp", args: [], env: {} });
  });

  test("a flag of the command before -- is a usage error, exit 2 without a request", async () => {
    const f = fakeFetch(() => ({ body: memory }));
    const r = await cli(["mcp", "set", "memory", "A knowledge graph.", "npx", "-y", "pkg@1.2.3"], f);
    expect(r.code).toBe(2);
    expect(r.stderr).toContain("'-y'");
    expect(f.requests.length).toBe(0);
  });

  test("a command left out is a usage error naming it", async () => {
    const f = fakeFetch(() => ({ body: memory }));
    const r = await cli(["mcp", "set", "memory", "A knowledge graph."], f);
    expect(r.code).toBe(2);
    expect(r.stderr).toContain("missing argument <command> (expected <name> <description> <command>)");
    expect(f.requests.length).toBe(0);
  });

  test("the help shows the command's arguments", async () => {
    const r = await cli(["mcp", "set", "--help"], fakeFetch(() => ({ body: memory })));
    expect(r.stdout).toContain("arm mcp set <name> <description> <command> [<args>...] [options]");
    expect(r.stdout).toMatch(/<args>\s+Its arguments/);
  });

  test("a reserved name is the server's 400, exit 1", async () => {
    const f = fakeFetch(() => ({
      status: 400,
      body: { error: { code: "RESERVED_MCP_SERVER_NAME", message: "'arm' is reserved.", details: {} } },
    }));
    const r = await cli(["mcp", "set", "arm", "Ours.", "arm-mcp"], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toContain("RESERVED_MCP_SERVER_NAME");
  });
});

describe("mcp delete", () => {
  test("DELETEs it", async () => {
    const f = fakeFetch(() => ({ status: 204 }));
    const r = await cli(["mcp", "delete", "memory"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("DELETE");
    expect(f.requests[0]!.url).toBe(`${url}/api/mcp-servers/memory`);
    expect(r.stdout).toBe("Deleted memory (sessions that took it keep their copy).");
  });

  test("an unknown server is a 404, exit 3", async () => {
    const f = fakeFetch(() => ({ status: 404, body: { error: { code: "MCP_SERVER_NOT_FOUND", message: "No MCP server 'nope'.", details: {} } } }));
    const r = await cli(["mcp", "delete", "nope"], f);
    expect(r.code).toBe(3);
    expect(r.stderr).toContain("MCP_SERVER_NOT_FOUND");
  });
});
