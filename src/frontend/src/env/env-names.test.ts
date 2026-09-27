import { describe, expect, it } from "vitest";
import { checkEnvRows, envNameProblem, isReservedEnvName, isValidEnvName } from "./env-names.js";

const invalid = "Names are letters, digits and _, not starting with a digit.";

describe("env names (as the server's EnvNames)", () => {
  it.each([
    "A",
    "_",
    "a1",
    "GIT_AUTHOR_NAME",
    "_X_9",
    "claude_x",
    "Arm_x",
    "path",
  ])("takes %s", (name) => {
    expect(isValidEnvName(name)).toBe(true);
    expect(envNameProblem(name, "given")).toBeUndefined();
  });

  it.each(["", "2-FAST", "1A", "A-B", "A B", " A", "Å", "A.B", "A=B"])("refuses %o", (name) => {
    expect(isValidEnvName(name)).toBe(false);
    expect(envNameProblem(name, "given")).toBe(invalid);
  });

  it.each([
    "PATH",
    "HOME",
    "USER",
    "LANG",
    "LC_ALL",
    "TERM",
    "TMPDIR",
    "DISABLE_UPDATES",
    "ENABLE_CLAUDEAI_MCP_SERVERS",
    "CLAUDE_",
    "CLAUDE_CONFIG_DIR",
    "ARM_",
    "ARM_TOKEN",
  ])("reserves %s", (name) => {
    expect(isReservedEnvName(name)).toBe(true);
  });

  it("is case-sensitive, like the server", () => {
    for (const name of ["Path", "home", "claude_x", "Arm_TOKEN", "CLAUDE", "ARM", "XARM_Y"]) {
      expect(isReservedEnvName(name)).toBe(false);
    }
  });

  it("says why, for a session's rows and for the register", () => {
    expect(envNameProblem("CLAUDE_CONFIG_DIR", "given")).toBe(
      "CLAUDE_CONFIG_DIR is set by ARM for its agents and can't be given.",
    );
    expect(envNameProblem("ARM_TOKEN", "registered")).toBe(
      "ARM_TOKEN is set by ARM for its agents and can't be registered.",
    );
  });
});

describe("checkEnvRows", () => {
  it("builds the env: names trimmed, values as typed, blank rows ignored", () => {
    expect(
      checkEnvRows([
        { name: " TASK_BRANCH ", value: "arm/x" },
        { name: "", value: "" },
        { name: " ", value: "  " },
        { name: "EMPTY", value: "" },
        { name: "SPACED", value: " a b " },
      ]),
    ).toEqual({
      problems: [undefined, undefined, undefined, undefined, undefined],
      env: { TASK_BRANCH: "arm/x", EMPTY: "", SPACED: " a b " },
    });
  });

  it("keeps every valid name as a key, __proto__ and constructor included", () => {
    const { env } = checkEnvRows([
      { name: "__proto__", value: "a" },
      { name: "constructor", value: "b" },
    ]);
    expect(Object.keys(env!)).toEqual(["__proto__", "constructor"]);
    expect(JSON.parse(JSON.stringify(env))).toEqual(
      JSON.parse('{"__proto__":"a","constructor":"b"}'),
    );
  });

  it("is empty for no rows", () => {
    expect(checkEnvRows([])).toEqual({ problems: [], env: {} });
  });

  it("flags each bad row: a bad or reserved name, a name used twice, a value without a name", () => {
    expect(
      checkEnvRows([
        { name: "CLAUDE_CONFIG_DIR", value: "/tmp/claude" },
        { name: "2-FAST", value: "yes" },
        { name: "A", value: "1" },
        { name: "a", value: "case differs" },
        { name: "A", value: "2" },
        { name: "", value: "orphan" },
      ]),
    ).toEqual({
      problems: [
        "CLAUDE_CONFIG_DIR is set by ARM for its agents and can't be given.",
        invalid,
        undefined,
        undefined,
        "A is given twice.",
        "Give this value a name.",
      ],
      env: undefined,
    });
  });
});
