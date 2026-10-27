import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

const pipelines = {
  name: "pipelines",
  description: "Drive Olve.Pipelines with the pl CLI.",
  default: true,
  source: { url: "git@github.com:o/skills.git", ref: "main", path: "pipelines" },
  updatedAt: "2026-10-27T10:00:00Z",
};
const pdf = {
  name: "pdf",
  description: "Read and fill PDFs.",
  default: false,
  source: { url: "https://github.com/anthropics/skills.git" },
  updatedAt: "2026-10-27T11:00:00Z",
};

describe("skill list", () => {
  test("GETs the skills and prints one line per skill, the source as url#ref path", async () => {
    const f = fakeFetch(() => ({ body: [pipelines, pdf] }));
    const r = await cli(["skill", "list"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/skills`);
    expect(r.stdout.split("\n")).toEqual([
      "SKILL      DEFAULT  SOURCE                                      DESCRIPTION",
      "pipelines  yes      git@github.com:o/skills.git#main pipelines  Drive Olve.Pipelines with the pl CLI.",
      "pdf        no       https://github.com/anthropics/skills.git    Read and fill PDFs.",
    ]);
  });

  test("is the default command of `arm skill`", async () => {
    const f = fakeFetch(() => ({ body: [pdf] }));
    const r = await cli(["skill"], f);
    expect(f.requests[0]!.url).toBe(`${url}/api/skills`);
    expect(r.code).toBe(0);
  });

  test("says so when none are installed", async () => {
    const r = await cli(["skill", "list"], fakeFetch(() => ({ body: [] })));
    expect(r.stdout).toBe("No skills installed.");
  });

  test("--json prints the payload", async () => {
    const f = fakeFetch(() => ({ body: [pipelines, pdf] }));
    const r = await cli(["skill", "list", "--json"], f);
    expect(JSON.parse(r.stdout)).toEqual([pipelines, pdf]);
  });
});

describe("skill get", () => {
  test("GETs by name and prints one field per line, leaving out an unset ref and path", async () => {
    const f = fakeFetch(() => ({ body: pipelines }));
    const r = await cli(["skill", "get", "pipelines"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.url).toBe(`${url}/api/skills/pipelines`);
    expect(r.stdout.split("\n")).toEqual([
      "name         pipelines",
      "description  Drive Olve.Pipelines with the pl CLI.",
      "default      yes",
      "url          git@github.com:o/skills.git",
      "ref          main",
      "path         pipelines",
      "updatedAt    2026-10-27T10:00:00Z",
    ]);
    const bare = await cli(["skill", "get", "pdf"], fakeFetch(() => ({ body: pdf })));
    expect(bare.stdout).not.toContain("ref");
    expect(bare.stdout).not.toContain("path");
  });

  test("an unknown skill is a 404, exit 3", async () => {
    const f = fakeFetch(() => ({ status: 404, body: { error: { code: "SKILL_NOT_FOUND", message: "No skill 'nope'.", details: {} } } }));
    const r = await cli(["skill", "get", "nope"], f);
    expect(r.code).toBe(3);
    expect(r.stderr).toContain("SKILL_NOT_FOUND");
  });
});

describe("skill install", () => {
  test("PUTs the source; no ref or path unless given, not default unless --default", async () => {
    const f = fakeFetch(() => ({ body: pdf }));
    const r = await cli(["skill", "install", "pdf", "https://github.com/anthropics/skills.git"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("PUT");
    expect(f.requests[0]!.url).toBe(`${url}/api/skills/pdf`);
    expect(f.requests[0]!.body).toEqual({ source: { url: "https://github.com/anthropics/skills.git" }, default: false });
    expect(r.stdout).toBe("Installed pdf.\nRead and fill PDFs.");
  });

  test("splits <url>#REF at the last # and sends --path and --default", async () => {
    const f = fakeFetch(() => ({ body: pipelines }));
    const r = await cli(
      ["skill", "install", "pipelines", "git@github.com:o/skills.git#main", "--path", "pipelines", "--default"],
      f,
    );
    expect(r.code).toBe(0);
    expect(f.requests[0]!.body).toEqual({
      source: { url: "git@github.com:o/skills.git", ref: "main", path: "pipelines" },
      default: true,
    });
    expect(r.stdout).toBe("Installed pipelines (every session that names no skills gets it).\nDrive Olve.Pipelines with the pl CLI.");
  });

  test("a # in the URL belongs to it when a ref follows", async () => {
    const f = fakeFetch(() => ({ body: pdf }));
    await cli(["skill", "install", "pdf", "https://host/b#c#release/1.0"], f);
    expect(f.requests[0]!.body).toEqual({ source: { url: "https://host/b#c", ref: "release/1.0" }, default: false });
  });

  for (const value of ["#main", "https://host/r#", "#"]) {
    test(`<url> ${value} is a usage error, exit 2 without a request`, async () => {
      const f = fakeFetch(() => ({ body: pdf }));
      const r = await cli(["skill", "install", "pdf", value], f);
      expect(r.code).toBe(2);
      expect(r.stderr).toContain(`<url> must be URL or URL#REF, got '${value}'`);
      expect(f.requests.length).toBe(0);
    });
  }

  test("a source that can't be fetched is the server's 400, exit 1", async () => {
    const f = fakeFetch(() => ({
      status: 400,
      body: { error: { code: "SKILL_SOURCE_FAILED", message: "fatal: repository not found", details: {} } },
    }));
    const r = await cli(["skill", "install", "pdf", "https://host/none.git"], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toContain("SKILL_SOURCE_FAILED");
  });
});

describe("skill remove", () => {
  test("DELETEs it", async () => {
    const f = fakeFetch(() => ({ status: 204 }));
    const r = await cli(["skill", "remove", "pdf"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("DELETE");
    expect(f.requests[0]!.url).toBe(`${url}/api/skills/pdf`);
    expect(r.stdout).toBe("Removed pdf (sessions that took it keep their copy).");
  });
});
