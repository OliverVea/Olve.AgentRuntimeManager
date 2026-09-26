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

  it.each([
    [{ prompt: "  " }, "Say what the agent should do."],
    [{ model: "fake" }, "The model is provider/model, e.g. claude/sonnet."],
    [{ model: "/fake" }, "The model is provider/model, e.g. claude/sonnet."],
    [{ timeout: "0" }, "The timeout must be a whole number of seconds (or empty for none)."],
    [{ timeout: "1.5" }, "The timeout must be a whole number of seconds (or empty for none)."],
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
