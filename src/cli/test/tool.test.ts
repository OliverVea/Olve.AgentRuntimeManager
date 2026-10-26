import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

const tools = [
  { name: "arm_bash", description: "Run a shell command." },
  { name: "arm_file_read", description: "Read a text file." },
];

describe("tool list", () => {
  test("GETs the tools and prints one line per tool", async () => {
    const f = fakeFetch(() => ({ body: tools }));
    const r = await cli(["tool", "list"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/tools`);
    expect(r.stdout.split("\n")).toEqual([
      "TOOL           DESCRIPTION",
      "arm_bash       Run a shell command.",
      "arm_file_read  Read a text file.",
    ]);
  });

  test("is the default command of `arm tool`", async () => {
    const f = fakeFetch(() => ({ body: tools }));
    const r = await cli(["tool"], f);
    expect(f.requests[0]!.url).toBe(`${url}/api/tools`);
    expect(r.code).toBe(0);
  });

  test("--json prints the payload", async () => {
    const f = fakeFetch(() => ({ body: tools }));
    const r = await cli(["tool", "list", "--json"], f);
    expect(JSON.parse(r.stdout)).toEqual(tools);
  });
});
