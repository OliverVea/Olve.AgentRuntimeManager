/** Typed readers for command option values; each throws {@link UsageError} for a malformed value. */
import { UsageError } from "./errors";
import type { OptionValues } from "./registry";

/** A string option's value; empty counts as unset. */
export function text(options: OptionValues, name: string): string | undefined {
  const raw = options[name];
  return typeof raw === "string" && raw !== "" ? raw : undefined;
}

/**
 * A string option that falls back to a default (the user's settings, a built-in value):
 * given, it must not be empty; with neither, it's a usage error that says how to set a default.
 */
export function textOrDefault(options: OptionValues, name: string, fallback: string | undefined): string {
  const raw = options[name];
  if (raw !== undefined) {
    const value = String(raw);
    if (value === "") throw new UsageError(`--${name} must not be empty`);
    return value;
  }
  if (fallback) return fallback;
  throw new UsageError(`missing --${name} (or set a default: arm config set ${name} <value>)`);
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

/** A comma-separated list (`--event a,b`); blanks are dropped, and an empty list is unset. */
export function commaList(options: OptionValues, name: string): string[] | undefined {
  const raw = options[name];
  if (raw === undefined) return undefined;
  const entries = String(raw)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  return entries.length ? entries : undefined;
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
