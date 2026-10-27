import type { Skill } from "@arm/client";
import { defaultTools, toolsField } from "../tools/tool-picks.js";

/** The registered skills flagged `default`: what a session that leaves `skills` out gets. */
export function defaultSkills(skills: readonly Skill[]): string[] {
  return defaultTools(skills);
}

/**
 * The create request's `skills` for a pick, by the tools' rule: left out (`undefined`) when it is
 * the registered defaults, or while there's no list; `[]` when nothing is picked (no skills);
 * else the picked names, in the list's order.
 */
export function skillsField(
  skills: readonly Skill[] | undefined,
  picked: ReadonlySet<string>,
): string[] | undefined {
  return toolsField(skills, picked);
}
