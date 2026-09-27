import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";
const id = "11111111-1111-4111-8111-111111111111";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

const conversation = {
  entries: [
    { seq: 1, kind: "prompt", text: "Fix the build" },
    { seq: 2, kind: "text", text: "Checking." },
    { seq: 3, kind: "tool_call", tool: "Bash", input: { command: "ls" }, toolId: "t1" },
    { seq: 4, kind: "tool_result", text: "a.txt", toolId: "t1", isError: false },
    { seq: 5, kind: "text", text: "Sub-step.", parentToolId: "t9" },
    { seq: 6, kind: "turn_end", text: "Fixed.", isError: false },
  ],
  turns: 1,
  messages: 1,
  toolCalls: 1,
  lastSeq: 6,
};

describe("session conversation", () => {
  test("GETs the conversation and prints it as text", async () => {
    const f = fakeFetch(() => ({ body: conversation }));
    const r = await cli(["session", "conversation", id], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.url).toBe(`${url}/api/sessions/${id}/conversation`);
    expect(r.stdout).toBe(
      [
        "▶ Prompt\n    Fix the build",
        "● Agent\n    Checking.",
        '→ Bash\n    {\n      "command": "ls"\n    }',
        "← Result\n    a.txt",
        "  │ ● Agent\n  │     Sub-step.",
        "■ Turn ended\n    Fixed.",
        "1 turn · 1 message · 1 tool call",
      ].join("\n\n"),
    );
  });

  test("a long tool result is cut, --json has it all", async () => {
    const long = Array.from({ length: 30 }, (_, i) => `line ${i + 1}`).join("\n");
    const body = { ...conversation, entries: [{ seq: 1, kind: "tool_result", text: long, toolId: "t1", isError: false }] };
    const f = fakeFetch(() => ({ body }));
    const r = await cli(["session", "conversation", id], f);
    expect(r.stdout).toContain("… 10 more lines (--json for all)");
    const json = await cli(["session", "conversation", id, "--json"], fakeFetch(() => ({ body })));
    expect(JSON.parse(json.stdout).entries[0].text).toBe(long);
  });

  test("no entries yet", async () => {
    const f = fakeFetch(() => ({ body: { entries: [], turns: 0, messages: 0, toolCalls: 0, lastSeq: 0 } }));
    const r = await cli(["session", "conversation", id], f);
    expect(r.stdout).toBe("No conversation yet.");
  });
});
