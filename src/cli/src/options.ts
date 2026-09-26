/** Typed readers for command option values; each throws {@link UsageError} for a malformed value. */
import { UsageError } from "./errors";
import type { OptionValues } from "./registry";

/** A string option's value; empty counts as unset. */
export function text(options: OptionValues, name: string): string | undefined {
  const raw = options[name];
  return typeof raw === "string" && raw !== "" ? raw : undefined;
}

/** A `true` boolean flag, else undefined (so unset flags are left out of request bodies). */
export function flag(options: OptionValues, name: string): true | undefined {
  return options[name] === true ? true : undefined;
}

/** An integer option within `[min, max]`. */
export function integer(
  options: OptionValues,
  name: string,
  { min, max }: { min: number; max?: number },
): number | undefined {
  const raw = options[name];
  if (raw === undefined) return undefined;
  const value = String(raw);
  const n = /^\d+$/.test(value) ? Number(value) : Number.NaN;
  if (Number.isNaN(n) || n < min || (max !== undefined && n > max)) {
    const expected =
      max !== undefined
        ? `an integer from ${min} to ${max}`
        : min === 1
          ? "a positive integer"
          : `an integer of at least ${min}`;
    throw new UsageError(`--${name} must be ${expected}, got '${value}'`);
  }
  return n;
}

/** A comma-separated list (`--tools a,b`); blanks are dropped, and an empty list is unset. */
export function commaList(options: OptionValues, name: string): string[] | undefined {
  const raw = options[name];
  if (raw === undefined) return undefined;
  const entries = String(raw)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  return entries.length ? entries : undefined;
}

/**
 * A repeatable `KEY<sep>VALUE` option (`--tag team:infra`, `--env K=V`) as a map, split at the
 * first separator. The key must be non-empty; the value may be empty; a key given twice is an error.
 */
export function pairs(
  options: OptionValues,
  name: string,
  separator: ":" | "=",
): Record<string, string> | undefined {
  const raw = options[name];
  if (raw === undefined) return undefined;
  const entries = Array.isArray(raw) ? raw : [String(raw)];
  const format = separator === ":" ? "key:value" : "KEY=VALUE";
  const result: Record<string, string> = {};
  for (const entry of entries) {
    const at = entry.indexOf(separator);
    if (at <= 0) throw new UsageError(`--${name} must be ${format}, got '${entry}'`);
    const key = entry.slice(0, at);
    if (Object.hasOwn(result, key)) throw new UsageError(`--${name} '${key}' is given more than once`);
    result[key] = entry.slice(at + 1);
  }
  return entries.length ? result : undefined;
}

/** A date or date-time (`2026-09-01`, `2026-09-01T12:00:00Z`), sent as an ISO 8601 UTC timestamp. */
export function dateTime(options: OptionValues, name: string): string | undefined {
  const value = text(options, name);
  if (value === undefined) return undefined;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    throw new UsageError(`--${name} must be a date or date-time (e.g. 2026-09-01 or 2026-09-01T12:00:00Z), got '${value}'`);
  }
  return date.toISOString();
}

/** One of a fixed set of values. */
export function oneOf<T extends string>(options: OptionValues, name: string, values: readonly T[]): T | undefined {
  const value = text(options, name);
  if (value === undefined) return undefined;
  if (!(values as readonly string[]).includes(value)) {
    throw new UsageError(`--${name} must be one of ${values.join(", ")}, got '${value}'`);
  }
  return value as T;
}
