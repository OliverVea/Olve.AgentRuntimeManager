import { describe, expect, test } from "bun:test";
import { ApiError, NetworkError, describeApiError, exitCodeForStatus } from "../src/errors";
import { formatJson, formatKeyValue, formatObject, formatTable, truncate } from "../src/output";

describe("formatTable", () => {
  test("pads columns, upper-cases headers, leaves no trailing spaces", () => {
    const rows = [
      { id: "a", text: "short" },
      { id: "longer-id", text: "x" },
    ];
    const table = formatTable(rows, [
      { header: "id", value: (r) => r.id },
      { header: "text", value: (r) => r.text },
    ]);
    expect(table).toBe(["ID         TEXT", "a          short", "longer-id  x"].join("\n"));
  });

  test("keeps rows on one line", () => {
    const table = formatTable([{ t: "a\nb\tc" }], [{ header: "t", value: (r) => r.t }]);
    expect(table.split("\n")).toHaveLength(2);
    expect(table).toContain("a b c");
  });
});

describe("formatKeyValue / formatObject", () => {
  test("aligns keys", () => {
    expect(formatKeyValue([["id", "1"], ["text", "hi"]])).toBe("id    1\ntext  hi");
  });

  test("objects print in property order; nested values as JSON; null as empty", () => {
    expect(formatObject({ a: 1, b: { c: 2 }, d: null })).toBe('a  1\nb  {"c":2}\nd');
  });
});

describe("truncate", () => {
  test("keeps short text, collapses whitespace, cuts long text with an ellipsis", () => {
    expect(truncate("a\n b", 10)).toBe("a b");
    expect(truncate("abcdefghij", 5)).toBe("abcd…");
  });
});

describe("formatJson", () => {
  test("is indented, round-trips", () => {
    const value = { items: [{ id: "1" }], n: 2 };
    const text = formatJson(value);
    expect(text).toContain('\n  "items"');
    expect(JSON.parse(text)).toEqual(value);
  });

  test("undefined becomes null (always valid JSON)", () => {
    expect(formatJson(undefined)).toBe("null");
  });
});

describe("errors", () => {
  test("the error envelope: code and message", () => {
    const body = { error: { code: "SESSION_NOT_FOUND", message: "no such session", details: {} } };
    expect(describeApiError(404, "", body)).toBe("404 Not Found: SESSION_NOT_FOUND: no such session");
  });

  test("several problems are listed under it, one per line", () => {
    const problems = [
      { code: "A", message: "first" },
      { code: "B", message: "second" },
    ];
    const body = { error: { code: "INVALID_REQUEST", message: "invalid", details: { problems } } };
    expect(describeApiError(400, "Bad Request", body)).toBe(
      "400 Bad Request: INVALID_REQUEST: invalid\n  - A: first\n  - B: second",
    );
  });

  test("an empty body, or one that isn't the envelope, falls back to the status", () => {
    expect(describeApiError(401, "", {})).toBe("401 Unauthorized");
    expect(describeApiError(502, "", "<html>Bad Gateway</html>")).toBe("502 Bad Gateway");
    expect(describeApiError(400, "", [{ message: "old shape" }])).toBe("400 Bad Request");
  });

  test("ApiError JSON is the envelope, or one built from the status otherwise", () => {
    const body = { error: { code: "X", message: "x", details: {} } };
    expect(new ApiError(404, "", body).toJSON()).toEqual(body);
    expect(new ApiError(401, "", {}).toJSON()).toEqual({
      error: { code: "HTTP_401", message: "401 Unauthorized", details: { status: 401 } },
    });
    expect(new ApiError(502, "", "<html/>").toJSON()).toEqual({
      error: { code: "HTTP_502", message: "502 Bad Gateway", details: { status: 502 } },
    });
  });

  test("exit codes by status: 404 → 3, 409 → 4, 503 → 5, others → 1", () => {
    expect([404, 409, 503, 400, 401, 500, 502].map(exitCodeForStatus)).toEqual([3, 4, 5, 1, 1, 1, 1]);
    expect(new ApiError(409, "", {}).exitCode).toBe(4);
    expect(new NetworkError("http://x", new Error("x")).exitCode).toBe(1);
  });

  test("NetworkError includes the URL and cause", () => {
    const e = new NetworkError("http://x/api", new TypeError("fetch failed", { cause: new Error("ECONNREFUSED") }));
    expect(e.message).toBe("could not reach http://x/api: fetch failed (ECONNREFUSED)");
  });
});
