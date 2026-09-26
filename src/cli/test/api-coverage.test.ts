import { describe, expect, test } from "bun:test";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

// The CLI must be up to date with the API on every push (docs/STANDARDS.md, Clients): every
// operation of the generated client has to be used by some command. A new operation in the
// contract fails this test (and so `mise run ci`, the pipeline gate) until the CLI covers it.
const sdk = readFileSync(join(here, "../../../artifacts/clients/ts/sdk.gen.ts"), "utf8");
const operations = [...sdk.matchAll(/^export const (\w+) = /gm)].map((m) => m[1]!);

const srcDir = join(here, "../src");
const sources = readdirSync(srcDir, { recursive: true })
  .filter((f) => String(f).endsWith(".ts"))
  .map((f) => readFileSync(join(srcDir, String(f)), "utf8"))
  .join("\n");

describe("the CLI covers the API", () => {
  test("the generated client has operations", () => {
    expect(operations.length > 0).toBe(true);
  });

  for (const operation of operations) {
    test(`${operation} is used by a command`, () => {
      expect(new RegExp(`\\b${operation}\\(`).test(sources)).toBe(true);
    });
  }
});
