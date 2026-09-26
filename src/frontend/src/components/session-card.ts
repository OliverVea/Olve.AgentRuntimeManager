import type { Session } from "@arm/client";
import { escapeHtml } from "../base-element.js";
import { clock, isEnded, modelLabel, relative, runningTime, shortId } from "../sessions/format.js";

export type TimesAs = "relative" | "absolute";

const icon = (name: string) => `<svg class="i"><use href="#i-${name}"/></svg>`;

/** The time after the status: running time (or since when), or how long an ended session ran. */
export function timeOf(session: Session, times: TimesAs, now: number): string {
  if (isEnded(session) || times === "relative") return runningTime(session, now);
  return `since ${clock(session.startedAt ?? session.createdAt, now)}`;
}

function timeTitle(session: Session, times: TimesAs, now: number): string {
  const parts = [
    `created ${clock(session.createdAt, now)}`,
    session.startedAt ? `started ${clock(session.startedAt, now)}` : "",
    session.endedAt ? `ended ${clock(session.endedAt, now)}` : "",
  ].filter(Boolean);
  return `${parts.join(" · ")}. Click to show ${times === "relative" ? "clock times" : "relative times"}.`;
}

/** Who ended it: the kill caller, else the source (`timeout`, `system`). */
function endedBy(session: Session): string {
  return session.killSource === "user" && session.killCaller
    ? session.killCaller
    : (session.killSource ?? "");
}

/** The short outcome after the status; the full text is its tooltip. */
function note(session: Session): string {
  switch (session.status) {
    case "queued":
      return session.queuePosition !== undefined
        ? `<span class="note">#${session.queuePosition} in queue</span>`
        : "";
    case "completed":
      return `<span class="note">exit ${session.exitCode ?? "?"}</span>`;
    case "cancelled":
    case "killed":
      return `<span class="note" title="${escapeHtml(session.killReason ?? "")}">by ${escapeHtml(endedBy(session))}</span>`;
    case "failed":
      return `<span class="note failed" title="${escapeHtml(session.error ?? "")}">${escapeHtml(session.error ?? "")}</span>`;
    default:
      return "";
  }
}

/** One session card: status · time · outcome, the task, then id · model · caller (· ended). */
export function sessionCard(session: Session, times: TimesAs, now: number): string {
  const ended = isEnded(session);
  const title = escapeHtml(timeTitle(session, times, now));
  const id = escapeHtml(session.id);
  const r1 = [
    `<span class="badge ${session.status}">${session.status}</span>`,
    `<button class="dur" data-dur="${id}" data-times title="${title}">${timeOf(session, times, now)}</button>`,
    note(session),
  ].filter(Boolean);
  const info = [
    `<span class="id" data-copy="${id}" title="Copy the full id (${id})">${shortId(session)}</span>`,
    `<span title="${escapeHtml(`${session.provider}/${session.model}`)}">${escapeHtml(modelLabel(session))}</span>`,
    `<span>${escapeHtml(session.caller)}</span>`,
    ended && session.endedAt
      ? `<button class="when" data-times title="${title}">ended ${
          times === "relative" ? relative(session.endedAt, now) : clock(session.endedAt, now)
        }</button>`
      : "",
  ];
  const stop = ended
    ? `<button class="stop" data-delete="${id}" title="Delete session" aria-label="Delete session">${icon("trash")}</button>`
    : session.status === "queued"
      ? `<button class="stop" data-kill="${id}" title="Cancel session" aria-label="Cancel session">${icon("x")}</button>`
      : `<button class="stop" data-kill="${id}" title="Kill session" aria-label="Kill session">${icon("x")}</button>`;

  return `<div class="card ${session.status}" data-open="${id}" tabindex="0" role="link" aria-label="Open session ${shortId(session)}">
      <div class="details">
        <div class="r1">${r1.join(`<span class="sep">·</span>`)}</div>
        <div class="task" title="${escapeHtml(session.prompt)}">${escapeHtml(session.prompt)}</div>
        <div class="info">${info.join("")}</div>
      </div>
      <div class="icons">${stop}</div>
    </div>`;
}

export const cardStyles = `
  :host { display: block; }
  button { font: inherit; color: inherit; cursor: pointer; }
  svg.i { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; flex: none; }

  .card { display: grid; grid-template-columns: 1fr auto; background: var(--card); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 9px; margin-bottom: 8px; cursor: pointer; transition: border-color .12s, box-shadow .12s; }
  .card:hover { border-color: var(--faint); box-shadow: 0 1px 4px rgba(0, 0, 0, .06); }
  .card:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
  .card.working { border-left-color: var(--working); } .card.queued { border-left-color: var(--queued); }
  .details { padding: 8px 14px; min-width: 0; }
  .details > div { height: var(--row); line-height: var(--row); white-space: nowrap; overflow: hidden; }
  .r1 { display: flex; align-items: center; gap: 8px; }
  .badge { font-size: 11px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase; border-radius: 4px; padding: 0 7px; line-height: 18px; flex: none; }
  .badge.working { background: var(--working-bg); color: var(--working); } .badge.queued { background: var(--queued-bg); color: var(--queued); }
  .badge.completed { background: var(--completed-bg); color: var(--completed); } .badge.killed { background: var(--killed-bg); color: var(--killed); }
  .badge.failed { background: var(--failed-bg); color: var(--failed); } .badge.cancelled { background: var(--queued-bg); color: var(--queued); }
  .dur, .when { font: inherit; font-variant-numeric: tabular-nums; color: var(--dim); flex: none; border: 0; background: none; padding: 0; cursor: pointer; }
  .dur:hover, .when:hover { text-decoration: underline dotted; }
  .r1 .sep { color: var(--faint); flex: none; margin: 0 -2px; }
  .note { color: var(--dim); font-size: 13px; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
  .note.failed { color: var(--failed); }
  .task { font-size: 14px; text-overflow: ellipsis; }
  .info { font: 12px var(--mono); color: var(--dim); }
  .info > span + span::before, .info > span + button::before { content: "·"; margin: 0 6px; color: var(--faint); }
  .info .when { font: inherit; }
  .r1.overflows, .info.overflows { -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 3em), transparent); mask-image: linear-gradient(to right, #000 calc(100% - 3em), transparent); }
  .info .id { cursor: copy; border-bottom: 1px dotted var(--faint); }
  .info .id:hover { color: var(--accent); }

  .icons { display: flex; align-items: center; gap: 1px; align-self: start; padding: 5px 6px 0 0; }
  .icons button { display: inline-grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 6px; background: transparent; color: var(--danger); padding: 0; }
  .icons button:hover { background: var(--danger-soft); color: var(--danger); }
  .icons svg.i { width: 15px; height: 15px; }

  .empty { color: var(--dim); text-align: center; padding: 34px; }
  .error { margin: 0 0 10px; padding: 8px 12px; border-radius: 8px; background: var(--danger-soft); color: var(--danger); font-size: 13px; }
  .footer-note { text-align: center; color: var(--dim); font-size: 13px; margin-top: 6px; }
  .footer-note button { border: 0; background: none; color: var(--accent); padding: 0; }
`;
