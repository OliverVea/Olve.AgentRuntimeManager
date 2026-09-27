import { describe, expect, it } from "vitest";
import { duration, parseModel } from "./format.js";
import { newSession, parseTimeout } from "./new-session.js";

const fields = { prompt: " fix it ", model: "fake/fake", caller: "", timeout: "" };

describe("newSession", () => {
  it("builds the create request; an empty caller is the fallback, no timeout is left out", () => {
    expect(newSession(fields, "oliver")).toEqual({
      ok: true,
      value: { prompt: "fix it", provider: "fake", model: "fake", caller: "oliver" },
    });
  });

  it("takes a caller and a timeout", () => {
    const parsed = newSession({ ...fields, caller: " ci ", timeout: "600" }, "oliver");
    expect(parsed).toMatchObject({ ok: true, value: { caller: "ci", timeoutSeconds: 600 } });
  });

  it("takes the session's own env and the registered variables it picks, leaving out empty ones", () => {
    const parsed = newSession(
      {
        ...fields,
        env: [
          { name: " TASK_BRANCH ", value: "arm/x" },
          { name: "", value: "" },
        ],
        useEnv: ["EXTRA_TOOLS"],
      },
      "oliver",
    );
    expect(parsed).toMatchObject({
      ok: true,
      value: { env: { TASK_BRANCH: "arm/x" }, useEnv: ["EXTRA_TOOLS"] },
    });
    const bare = newSession({ ...fields, env: [{ name: " ", value: "" }], useEnv: [] }, "oliver");
    expect(bare.ok && ("env" in bare.value || "useEnv" in bare.value)).toBe(false);
  });

  it.each([
    [{ prompt: "  " }, "Say what the agent should do."],
    [{ model: "fake" }, "The model is provider/model, e.g. claude/sonnet."],
    [{ model: "/fake" }, "The model is provider/model, e.g. claude/sonnet."],
    [{ timeout: "0" }, "The timeout must be a whole number of seconds (or empty for none)."],
    [{ timeout: "1.5" }, "The timeout must be a whole number of seconds (or empty for none)."],
    [
      { env: [{ name: "ARM_TOKEN", value: "x" }] },
      "ARM_TOKEN is set by ARM for its agents and can't be given.",
    ],
    [
      {
        env: [
          { name: "A", value: "1" },
          { name: "A", value: "2" },
        ],
      },
      "A is given twice.",
    ],
    [{ env: [{ name: "", value: "orphan" }] }, "Give this value a name."],
  ])("rejects %o", (change, problem) => {
    expect(newSession({ ...fields, ...change }, "oliver")).toEqual({ ok: false, problem });
  });
});

describe("parsing and formatting", () => {
  it("splits provider/model at the first slash", () => {
    expect(parseModel("claude/org/model-x")).toEqual({ provider: "claude", model: "org/model-x" });
  });

  it("reads an empty timeout as none", () => {
    expect(parseTimeout(" ")).toEqual({ ok: true, value: undefined });
  });

  it.each([
    [42, "42s"],
    [300, "5m"],
    [3600, "1h"],
    [7620, "2h 7m"],
  ])("formats %i seconds as %s", (seconds, text) => {
    expect(duration(seconds)).toBe(text);
  });
});
