// C# naming helpers.

const KEYWORDS = new Set(
  (
    "abstract as base bool break byte case catch char checked class const continue decimal default delegate do " +
    "double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface " +
    "internal is lock long namespace new null object operator out override params private protected public readonly " +
    "ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong " +
    "unchecked unsafe ushort using virtual void volatile while"
  ).split(" "),
);

/** Splits a wire name (`pageSize`, `page_size`, `x-request-id`) into words and joins them in PascalCase. */
export function pascal(name) {
  return String(name)
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join("")
    .replace(/^(\d)/, "_$1");
}

/** camelCase C# identifier, escaped with `@` when it collides with a keyword. */
export function camel(name) {
  const p = pascal(name);
  const c = p.charAt(0).toLowerCase() + p.slice(1);
  return KEYWORDS.has(c) ? `@${c}` : c;
}

/** Ordinal (culture-independent) comparison, so output order is byte-stable everywhere. */
export function ordinal(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** A C# string literal. */
export function str(value) {
  return JSON.stringify(String(value));
}

/** One-line XML-doc-safe text. */
export function xmlText(text) {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\s*\n\s*/g, " ")
    .trim();
}
