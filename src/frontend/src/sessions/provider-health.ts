import type { ProviderHealth } from "@arm/client";
import { clock } from "./format.js";

/** Whether sessions wait for it: anything but `available`. */
export function isPaused(health: ProviderHealth | undefined): health is ProviderHealth {
  return health !== undefined && health.status !== "available";
}

/** `until 22:00` (limited) or `next try 19:44` (unreachable); empty when there's no time. */
export function untilText(health: ProviderHealth, now: number): string {
  if (!health.until) return "";
  return `${health.status === "limited" ? "until" : "next try"} ${clock(health.until, now)}`;
}

/** The popover's time line: `since 19:30 · back at 22:00`. */
export function timesText(health: ProviderHealth, now: number): string {
  const since = health.since ? `since ${clock(health.since, now)}` : "";
  const until =
    health.status === "unauthorized"
      ? "until ARM restarts with a new token"
      : health.until
        ? `${health.status === "limited" ? "back at" : "next try"} ${clock(health.until, now)}`
        : "";
  return [since, until].filter(Boolean).join(" · ");
}

/** What a queued session waits for: `waiting for claude: its usage limit resets at 22:00`. */
export function waitingText(health: ProviderHealth, now: number): string {
  const until = health.until ? clock(health.until, now) : "";
  switch (health.status) {
    case "limited":
      return `waiting for ${health.provider}: its usage limit resets${until ? ` at ${until}` : ""}`;
    case "unreachable":
      return `waiting for ${health.provider}: unreachable${until ? `, next try ${until}` : ""}`;
    default:
      return `waiting for ${health.provider}: its token was rejected`;
  }
}

/** The composer's hint: `claude is limited until 22:00: it will queue`. */
export function willQueueText(health: ProviderHealth, now: number): string {
  const until = untilText(health, now);
  const when = !until ? "" : health.status === "limited" ? ` ${until}` : ` (${until})`;
  return `${health.provider} is ${health.status}${when}: it will queue`;
}
