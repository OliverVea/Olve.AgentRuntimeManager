import { describe, expect, test } from "bun:test";
import { ApiError, NetworkError, describeApiError } from "../src/errors";
import { formatJson, formatKeyValue, formatObject, formatTable } from "../src/output";

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
  test("ResultProblem arrays are joined", () => {
    const body = [
      { message: "Text is required", tags: null, severity: 0, source: null, exceptionSummary: null },
      { message: "Too long", tags: null, severity: 0, source: null, exceptionSummary: null },
    ];
    expect(describeApiError(400, "Bad Request", body)).toBe("400 Bad Request: Text is required; Too long");
  });

  test("the SPEC envelope is understood", () => {
    const body = { error: { code: "NOT_FOUND", message: "no such message", details: {} } };
    expect(describeApiError(404, "", body)).toBe("404 Not Found: NOT_FOUND: no such message");
  });

  test("an empty body falls back to the status", () => {
    expect(describeApiError(401, "", {})).toBe("401 Unauthorized");
  });

  test("ApiError JSON is the body, or an envelope when empty", () => {
    expect(new ApiError(404, "", [{ message: "x" }]).toJSON()).toEqual([{ message: "x" }]);
    expect(new ApiError(401, "", {}).toJSON()).toEqual({
      error: { code: "HTTP_401", message: "401 Unauthorized", details: { status: 401 } },
    });
  });

  test("NetworkError includes the URL and cause", () => {
    const e = new NetworkError("http://x/api", new TypeError("fetch failed", { cause: new Error("ECONNREFUSED") }));
    expect(e.message).toBe("could not reach http://x/api: fetch failed (ECONNREFUSED)");
  });
});
