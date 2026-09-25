/** Formatting for `--pretty` (tables, key/value) and `--json`. Pure functions, no I/O. */

export function formatJson(value: unknown): string {
  return JSON.stringify(value ?? null, null, 2);
}

/** Single-line cell text: newlines and tabs collapsed so a row stays one line. */
function cell(value: unknown): string {
  if (value === undefined || value === null) return "";
  const text = typeof value === "object" ? JSON.stringify(value) : String(value);
  return text.replace(/[\r\n\t]+/g, " ");
}

export type Column<T> = {
  header: string;
  value: (row: T) => unknown;
};

/** Left-aligned table with an upper-case header row; the last column is not padded. */
export function formatTable<T>(rows: readonly T[], columns: readonly Column<T>[]): string {
  const matrix = [
    columns.map((c) => c.header.toUpperCase()),
    ...rows.map((row) => columns.map((c) => cell(c.value(row)))),
  ];
  const widths = columns.map((_, i) => Math.max(...matrix.map((r) => r[i]!.length)));
  return matrix
    .map((r) =>
      r
        .map((text, i) => (i === r.length - 1 ? text : text.padEnd(widths[i]!)))
        .join("  ")
        .trimEnd(),
    )
    .join("\n");
}

/** Aligned `key  value` lines for a single object. */
export function formatKeyValue(entries: ReadonlyArray<readonly [string, unknown]>): string {
  const width = Math.max(0, ...entries.map(([k]) => k.length));
  return entries.map(([k, v]) => `${k.padEnd(width)}  ${cell(v)}`.trimEnd()).join("\n");
}

/** Key/value for any plain object, in property order. */
export function formatObject(value: object): string {
  return formatKeyValue(Object.entries(value));
}
