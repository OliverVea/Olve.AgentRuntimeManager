import type { Tool } from "@arm/client";

/** What the rules read of a tool: its name and whether the server gives it by default (a skill has the same). */
type Pickable = Pick<Tool, "name" | "default">;

/** The server's default tools: what a session that leaves `tools` out gets. */
export function defaultTools(tools: readonly Pickable[]): string[] {
  return tools.filter((t) => t.default).map((t) => t.name);
}

/**
 * The create request's `tools` for a pick: left out (`undefined`) when it is the server's
 * defaults, or while there's no list (nothing could be picked from it); `[]` when nothing is
 * picked (no tools at all); else the picked names, in the list's order.
 */
export function toolsField(
  tools: readonly Pickable[] | undefined,
  picked: ReadonlySet<string>,
): string[] | undefined {
  if (!tools) return undefined;
  const names = tools.filter((t) => picked.has(t.name)).map((t) => t.name);
  const defaults = defaultTools(tools);
  if (names.length === defaults.length && defaults.every((d) => picked.has(d))) return undefined;
  return names;
}
