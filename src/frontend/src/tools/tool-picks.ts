import type { Tool } from "@arm/client";

/** The server's default tools: what a session that leaves `tools` out gets. */
export function defaultTools(tools: readonly Tool[]): string[] {
  return tools.filter((t) => t.default).map((t) => t.name);
}

/**
 * The create request's `tools` for a pick: left out (`undefined`) when it is the server's
 * defaults, or while there's no list (nothing could be picked from it); `[]` when nothing is
 * picked (no tools at all); else the picked names, in the list's order.
 */
export function toolsField(
  tools: readonly Tool[] | undefined,
  picked: ReadonlySet<string>,
): string[] | undefined {
  if (!tools) return undefined;
  const names = tools.filter((t) => picked.has(t.name)).map((t) => t.name);
  const defaults = defaultTools(tools);
  if (names.length === defaults.length && defaults.every((d) => picked.has(d))) return undefined;
  return names;
}
