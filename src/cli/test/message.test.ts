import { describe, expect, test } from "bun:test";
import { fakeFetch, runCli } from "./helpers";

const url = "http://arm.test";
const id = "3f2c1a9e-8d4b-4c7a-9e21-5b6d7f8a9c0d";
const message = { id, text: "hello world" };
const page = {
  items: [message, { id: "0b1c", text: "second\nline" }],
  pageNumber: 1,
  pageSize: 2,
  totalCount: 3,
  totalPages: 2,
  hasNextPage: true,
};
const problems = (text: string) => [
  { message: text, tags: null, severity: 0, source: null, exceptionSummary: null },
];

const cli = (argv: string[], f: ReturnType<typeof fakeFetch>) =>
  runCli([...argv, "--url", url, "--token", "tok"], { fetch: f.fetch });

describe("message list", () => {
  test("sends GET with query and bearer; prints a table and page footer", async () => {
    const f = fakeFetch(() => ({ body: page }));
    const r = await cli(["message", "list", "--page", "1", "--page-size", "2"], f);
    expect(r.code).toBe(0);
    expect(f.requests).toHaveLength(1);
    const req = f.requests[0]!;
    expect(req.method).toBe("GET");
    expect(req.url).toBe(`${url}/api/messages?page=1&pageSize=2`);
    expect(req.headers.get("authorization")).toBe("Bearer tok");
    expect(r.stdout).toBe(
      [
        "ID                                    TEXT",
        `${id}  hello world`,
        "0b1c                                  second line",
        "",
        "Page 1 of 2 · 3 messages · next: --page 2",
      ].join("\n"),
    );
    expect(r.stderr).toBe("");
  });

  test("omits unset query parameters", async () => {
    const f = fakeFetch(() => ({ body: { ...page, items: [] , totalCount: 0 } }));
    const r = await cli(["message", "list"], f);
    expect(f.requests[0]!.url).toBe(`${url}/api/messages`);
    expect(r.stdout).toBe("No messages.");
  });

  test("--json prints the raw page", async () => {
    const f = fakeFetch(() => ({ body: page }));
    const r = await cli(["message", "list", "--json"], f);
    expect(r.code).toBe(0);
    expect(JSON.parse(r.stdout)).toEqual(page);
  });
});

describe("message get", () => {
  test("GETs by id and prints key/value", async () => {
    const f = fakeFetch(() => ({ body: message }));
    const r = await cli(["message", "get", id], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("GET");
    expect(f.requests[0]!.url).toBe(`${url}/api/messages/${id}`);
    expect(r.stdout).toBe(`id    ${id}\ntext  hello world`);
  });

  test("escapes the id in the path", async () => {
    const f = fakeFetch(() => ({ body: message }));
    await cli(["message", "get", "a/b c"], f);
    expect(f.requests[0]!.url).toBe(`${url}/api/messages/a%2Fb%20c`);
  });

  test("404 with problems: readable error on stderr, exit 1", async () => {
    const f = fakeFetch(() => ({ status: 404, body: problems("Message not found") }));
    const r = await cli(["message", "get", id], f);
    expect(r.code).toBe(1);
    expect(r.stdout).toBe("");
    expect(r.stderr).toBe("error: 404 Not Found: Message not found");
  });

  test("404 with --json: the error body as JSON on stderr", async () => {
    const f = fakeFetch(() => ({ status: 404, body: problems("Message not found") }));
    const r = await cli(["message", "get", id, "--json"], f);
    expect(r.code).toBe(1);
    expect(r.stdout).toBe("");
    expect(JSON.parse(r.stderr)).toEqual(problems("Message not found"));
  });
});

describe("message create", () => {
  test("POSTs JSON {text} and prints the created message", async () => {
    const f = fakeFetch(() => ({ body: message }));
    const r = await cli(["message", "create", "hello world"], f);
    expect(r.code).toBe(0);
    const req = f.requests[0]!;
    expect(req.method).toBe("POST");
    expect(req.url).toBe(`${url}/api/messages`);
    expect(req.headers.get("content-type")).toContain("application/json");
    expect(req.body).toEqual({ text: "hello world" });
    expect(r.stdout).toContain(`id    ${id}`);
  });

  test("400 validation problems are joined", async () => {
    const f = fakeFetch(() => ({ status: 400, body: [...problems("Text is required"), ...problems("Too short")] }));
    const r = await cli(["message", "create", ""], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe("error: 400 Bad Request: Text is required; Too short");
  });

  test("401 with an empty body: status line, and a JSON envelope with --json", async () => {
    const f = fakeFetch(() => ({ status: 401 }));
    const r = await cli(["message", "create", "x"], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe("error: 401 Unauthorized");
    const j = await cli(["message", "create", "x", "--json"], f);
    expect(j.code).toBe(1);
    expect(JSON.parse(j.stderr)).toEqual({
      error: { code: "HTTP_401", message: "401 Unauthorized", details: { status: 401 } },
    });
  });
});

describe("message update", () => {
  test("PUTs {text} to the id", async () => {
    const f = fakeFetch((req) => ({ body: { id, ...(req.body as object) } }));
    const r = await cli(["message", "update", id, "new text", "--json"], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("PUT");
    expect(f.requests[0]!.url).toBe(`${url}/api/messages/${id}`);
    expect(f.requests[0]!.body).toEqual({ text: "new text" });
    expect(JSON.parse(r.stdout)).toEqual({ id, text: "new text" });
  });
});

describe("message delete", () => {
  test("DELETEs the id; empty 200 body", async () => {
    const f = fakeFetch(() => ({ status: 200 }));
    const r = await cli(["message", "delete", id], f);
    expect(r.code).toBe(0);
    expect(f.requests[0]!.method).toBe("DELETE");
    expect(f.requests[0]!.url).toBe(`${url}/api/messages/${id}`);
    expect(r.stdout).toBe(`Deleted message ${id}`);
    const j = await cli(["message", "delete", id, "--json"], f);
    expect(JSON.parse(j.stdout)).toEqual({});
  });
});

describe("network failures", () => {
  test("a fetch that throws exits 1 with the URL in the message", async () => {
    const f = fakeFetch(() => new TypeError("fetch failed"));
    const r = await cli(["message", "list"], f);
    expect(r.code).toBe(1);
    expect(r.stderr).toBe(`error: could not reach ${url}/api/messages: fetch failed`);
  });

  test("with --json it is a NETWORK_ERROR envelope", async () => {
    const f = fakeFetch(() => new TypeError("fetch failed"));
    const r = await cli(["message", "list", "--json"], f);
    expect(r.code).toBe(1);
    expect((JSON.parse(r.stderr) as { error: { code: string } }).error.code).toBe("NETWORK_ERROR");
  });
});
