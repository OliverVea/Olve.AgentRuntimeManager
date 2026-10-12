import type { Activity, ActivityKind } from "@arm/client";

/** A mark per kind of line, as the web UI's log column shows them. */
const marks: Record<ActivityKind, string> = { text: "›", tool: "⚙", error: "!" };

/** The activity as one line each, oldest first: `› text`, `⚙ Bash → npx mise run ci`, `! error`. */
export function formatActivity(a: Activity): string {
  if (a.lines.length === 0) return "No activity yet.";
  return a.lines.map((line) => `${marks[line.kind]} ${line.text}`).join("\n");
}
