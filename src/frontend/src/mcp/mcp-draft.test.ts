import type { McpServer } from "@arm/client";
import { describe, expect, it } from "vitest";
import {
  checkDraft,
  commandLine,
  draftOf,
  mcpNameProblem,
  parseArgs,
  parseEnv,
  sourceText,
} from "./mcp-draft.js";

const invalid =
  "Names are lowercase letters, digits and -, starting with a letter, at most 32 characters.";
const reserved = "arm and names starting with arm- are reserved for ARM.";

describe("MCP server names", () => {
  it.each([
    ["github", undefined],
    ["a", undefined],
    ["my-server-2", undefined],
    ["armada", undefined],
    ["a".repeat(32), undefined],
    ["a".repeat(33), invalid],
    ["", invalid],
    ["2fast", invalid],
    ["-x", invalid],
    ["GitHub", invalid],
    ["my_server", invalid],
    ["a b", invalid],
    ["arm", reserved],
    ["arm-tools", reserved],
  ])("%o: %o", (name, problem) => {
    expect(mcpNameProblem(name)).toBe(problem);
  });
});

describe("a server's fields", () => {
  it.each([
    ["", []],
    ["-y\npkg@1.2.3", ["-y", "pkg@1.2.3"]],
    ["-y\r\npkg@1.2.3\r\n", ["-y", "pkg@1.2.3"]],
    ["\n  \n--name=a b\n\n", ["--name=a b"]],
    [" keeps its spaces ", [" keeps its spaces "]],
  ])("arguments %o", (text, args) => {
    expect(parseArgs(text)).toEqual(args);
  });

  it.each([
    ["", { env: {} }],
    ["TOKEN=abc", { env: { TOKEN: "abc" } }],
    // The first = splits; the value is as typed (tokens hold = and spaces), the name trimmed.
    [" TOKEN =a=b c \r\nEMPTY=\n\n", { env: { TOKEN: "a=b c ", EMPTY: "" } }],
    ["__proto__=x", { env: Object.fromEntries([["__proto__", "x"]]) }],
    ["TOKEN", { problem: "Env lines are NAME=value." }],
    ["=x", { problem: "Names are letters, digits and _, not starting with a digit." }],
    ["1A=x", { problem: "Names are letters, digits and _, not starting with a digit." }],
    ["PATH=/opt/bin", { problem: "PATH is set by ARM for its agents and can't be given." }],
    ["ARM_X=1", { problem: "ARM_X is set by ARM for its agents and can't be given." }],
    ["A=1\nA=2", { problem: "A is given twice." }],
  ])("env %o", (text, result) => {
    const parsed = parseEnv(text);
    expect(parsed).toEqual(result);
    // In order, and a key like `__proto__` kept as one.
    if ("env" in parsed && "env" in result)
      expect(Object.keys(parsed.env)).toEqual(Object.keys(result.env));
  });

  it("needs a description and a command, and env lines that parse", () => {
    const draft = { description: " GitHub. ", command: " npx ", args: "-y\npkg", env: "T=1" };
    expect(checkDraft(draft)).toEqual({
      body: { description: "GitHub.", command: "npx", args: ["-y", "pkg"], env: { T: "1" } },
      problem: "",
      bad: undefined,
    });
    expect(checkDraft({ ...draft, description: " " })).toEqual({
      body: undefined,
      problem: "",
      bad: undefined,
    });
    expect(checkDraft({ ...draft, command: "" }).body).toBeUndefined();
    expect(checkDraft({ ...draft, env: "T" })).toEqual({
      body: undefined,
      problem: "Env lines are NAME=value.",
      bad: "env",
    });
  });

  it("gives back the same server from its own fields", () => {
    const server: McpServer = {
      name: "x",
      description: "X.",
      command: "uvx",
      args: ["pkg==1", "--zone=Europe/Copenhagen", "a b"],
      env: { TOKEN: "a=b ", OTHER: "" },
      updatedAt: "2026-11-03T12:00:00Z",
    };
    const { name, updatedAt, ...rest } = server;
    expect(checkDraft(draftOf(server)).body).toEqual(rest);
  });

  it("sends what wasn't touched as the server has it, even what the fields can't hold", () => {
    const server: McpServer = {
      name: "x",
      description: " X. ",
      command: "tool",
      args: ["--prefix", "", "--name", "a\nb", " "],
      env: { KEY: "-----BEGIN-----\nabc\n-----END-----\r" },
      updatedAt: "2026-11-03T12:00:00Z",
    };
    const draft = draftOf(server);
    expect(checkDraft(draft, server).body).toEqual({
      description: " X. ",
      command: "tool",
      args: server.args,
      env: server.env,
    });
    // Without the server the same text reads as what it says (and the env doesn't parse).
    expect(checkDraft(draft).problem).toBe("Env lines are NAME=value.");
    // A field that was changed is read from its text; the others stay as they were.
    expect(checkDraft({ ...draft, args: "--prefix\n\n--name" }, server).body).toEqual({
      description: " X. ",
      command: "tool",
      args: ["--prefix", "--name"],
      env: server.env,
    });
    expect(checkDraft({ ...draft, description: " Y. " }, server).body?.description).toBe("Y.");
  });

  it("writes the command line, quoting what has spaces, then the env's names", () => {
    expect(commandLine({ command: "npx", args: ["-y", "pkg@1"] })).toBe("npx -y pkg@1");
    expect(commandLine({ command: "x", args: ["a b", ""] })).toBe('x "a b" ""');
    expect(sourceText({ command: "npx", args: ["-y"], env: {} })).toBe("npx -y");
    expect(sourceText({ command: "npx", args: [], env: { A: "secret", B: "" } })).toBe(
      "npx  A=… B=…",
    );
  });
});
