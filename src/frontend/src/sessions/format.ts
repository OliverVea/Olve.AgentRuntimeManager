import type { Session, SessionStatus } from "@arm/client";

/** Statuses a session never leaves. */
export const endedStatuses: readonly SessionStatus[] = [
  "completed",
  "cancelled",
  "killed",
  "failed",
];

/** Statuses of the sessions the Overview shows. */
export const activeStatuses: readonly SessionStatus[] = ["queued", "working"];

export function isEnded(session: Session): boolean {
  return endedStatuses.includes(session.status);
}

/** The first 8 characters of the id: enough to tell sessions apart on screen. */
export function shortId(session: Session): string {
  return session.id.slice(0, 8);
}

/** A duration in seconds: `42s`, `5m`, `1h`, `2h 7m`. */
export function duration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const rest = minutes % 60;
  return `${Math.floor(minutes / 60)}h${rest ? ` ${rest}m` : ""}`;
}

function secondsBetween(from: string, to: number): number {
  return Math.max(0, Math.round((to - Date.parse(from)) / 1000));
}

/** How long a session has run (or waited, while queued), or how long it ran once ended. */
export function runningTime(session: Session, now: number): string {
  const from = session.startedAt ?? session.createdAt;
  const to = session.endedAt ? Date.parse(session.endedAt) : now;
  return duration(secondsBetween(from, to));
}

/** `just now`, `5m ago`, `3h ago`. */
export function relative(iso: string, now: number): string {
  const minutes = Math.round(secondsBetween(iso, now) / 60);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  return `${Math.round(minutes / 60)}h ago`;
}

/** A clock time: `08:31` today, `Sep 25 08:31` on other days. */
export function clock(iso: string, now: number): string {
  const date = new Date(iso);
  const hm = date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
  if (date.toDateString() === new Date(now).toDateString()) return hm;
  return `${date.toLocaleDateString([], { month: "short", day: "numeric" })} ${hm}`;
}

/** The model as the card shows it: `model (provider)`. Display names come with M4b. */
export function modelLabel(session: Pick<Session, "provider" | "model">): string {
  return `${session.model} (${session.provider})`;
}

/** Splits the composer's `provider/model` into its parts; undefined when either is missing. */
export function parseModel(value: string): { provider: string; model: string } | undefined {
  const slash = value.indexOf("/");
  if (slash < 0) return undefined;
  const provider = value.slice(0, slash).trim();
  const model = value.slice(slash + 1).trim();
  return provider && model ? { provider, model } : undefined;
}
