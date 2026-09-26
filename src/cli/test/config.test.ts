import { beforeEach, describe, expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";
const session = {
  id: "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  status: "working",
  prompt: "hi",
  provider: "fake",
  model: "fake",
  caller: "me",
  timeoutSeconds: 600,
  createdAt: "2026-09-26T08:00:00Z",
};

// A fresh ARM_HOME per test; the previous one is removed when the next test starts.
let home = "";
beforeEach(() => {
  if (home) rmSync(home, { recursive: true, force: true });
  home = mkdtempSync(join(tmpdir(), "arm-home-"));
});

const env = (extra: Record<string, string> = {}) => ({ ARM_HOME: home, ...extra });

describe("arm config", () => {
  test("set, get, list and unset round-trip through config.json", async () => {
    expect((await runCli(["config", "set", "model", "sonnet"], { env: env() })).code).toBe(0);
    expect((await runCli(["config", "set", "url", "https://arm-beta.ovea.pro"], { env: env() })).code).toBe(0);
    expect(JSON.parse(readFileSync(join(home, "config.json"), "utf8"))).toEqual({
      model: "sonnet",
      url: "https://arm-beta.ovea.pro",
    });

    expect((await runCli(["config", "get", "model"], { env: env() })).stdout).toBe("sonnet");
    const list = await runCli(["config"], { env: env() });
    expect(list.stdout).toContain("model     sonnet");
    expect(list.stdout).toContain("caller    (not set; env ARM_CALLER)");
    expect(list.stdout).toContain(`file: ${join(home, "config.json")}`);

    expect((await runCli(["config", "unset", "model"], { env: env() })).code).toBe(0);
    expect((await runCli(["config", "get", "model"], { env: env() })).stdout).toBe("");
  });

  const invalid: Array<[string[], string]> = [
    [["config", "set", "colour", "red"], "unknown setting 'colour'"],
    [["config", "set", "url", "arm.example"], "invalid URL 'arm.example'"],
    [["config", "set", "caller", ""], "caller must not be empty"],
  ];
  for (const [argv, message] of invalid) {
    test(`${argv.join(" ")} is a usage error`, async () => {
      const r = await runCli(argv, { env: env() });
      expect(r.code).toBe(2);
      expect(r.stderr).toContain(message);
    });
  }

  test("a malformed config.json is a usage error, not a crash", async () => {
    writeFileSync(join(home, "config.json"), "{ nope");
    const r = await runCli(["session", "get", session.id], { env: env() });
    expect(r.code).toBe(2);
    expect(r.stderr).toContain("config.json is not valid JSON");
  });
});

describe("defaults: flag, then env, then config.json, then built-in", () => {
  const create = async (argv: string[], extra: Record<string, string> = {}) => {
    const f = fakeFetch(() => ({ status: 201, body: session }));
    const r = await runCli(["session", "create", "hi", "--token", "t", ...argv], { env: env(extra), fetch: f.fetch });
    return { r, req: f.requests[0] };
  };

  test("with nothing set: the built-in provider and model, the OS user as caller, localhost", async () => {
    const { r, req } = await create([], { USER: "oliver" });
    expect(r.code).toBe(0);
    expect(req!.url).toBe("http://localhost:5000/api/sessions");
    expect(req!.body).toEqual({ prompt: "hi", provider: "claude", model: "sonnet", caller: "oliver" });
  });

  test("config.json supplies url, provider, model and caller", async () => {
    writeFileSync(join(home, "config.json"), JSON.stringify({ url, provider: "p", model: "m", caller: "c" }));
    const { req } = await create([], { USER: "oliver" });
    expect(req!.url).toBe(`${url}/api/sessions`);
    expect(req!.body).toEqual({ prompt: "hi", provider: "p", model: "m", caller: "c" });
  });

  test("env beats config.json, and flags beat both", async () => {
    writeFileSync(join(home, "config.json"), JSON.stringify({ url, model: "from-file", caller: "file" }));
    const { req } = await create(["--caller", "flag"], { ARM_MODEL: "from-env", ARM_CALLER: "env" });
    expect(req!.body).toEqual({ prompt: "hi", provider: "claude", model: "from-env", caller: "flag" });
  });

  test("kill takes its caller from the same defaults", async () => {
    writeFileSync(join(home, "config.json"), JSON.stringify({ url, caller: "c" }));
    const f = fakeFetch(() => ({ body: { ...session, status: "killed" } }));
    await runCli(["session", "kill", session.id, "--token", "t"], { env: env(), fetch: f.fetch });
    expect(f.requests[0]!.body).toEqual({ caller: "c" });
  });
});
