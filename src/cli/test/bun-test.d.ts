// Minimal typings for the parts of `bun:test` these tests use, so `tsc --noEmit` can check the
// tests without adding a bun-types dependency.
declare module "bun:test" {
  interface Matchers {
    not: Matchers;
    toBe(expected: unknown): void;
    toEqual(expected: unknown): void;
    toContain(expected: unknown): void;
    toMatch(expected: RegExp | string): void;
    toThrow(expected?: unknown): void;
    toBeUndefined(): void;
    toHaveLength(length: number): void;
  }
  export function describe(name: string, fn: () => void): void;
  export function test(name: string, fn: () => void | Promise<unknown>): void;
  export function beforeEach(fn: () => void | Promise<unknown>): void;
  export function expect(actual: unknown): Matchers;
}
