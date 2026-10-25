import type { ActivityLine, Provider, ProviderHealth, Session } from "@arm/client";
import { escapeHtml } from "../base-element.js";
import { linkify } from "../sessions/conversation.js";
import { clock, isEnded, modelLabel, relative, runningTime, shortId } from "../sessions/format.js";
import { isPaused, waitingText } from "../sessions/provider-health.js";

export type TimesAs = "relative" | "absolute";

/** A working session's log column: its latest activity (undefined until read), wrapped, expanded. */
export type LogView = { lines: readonly ActivityLine[] | undefined; wrap: boolean; open: boolean };

/**
 * Lines a log shows, and so asks for: 3, 8 wrapped (the box keeps its height; older lines leave at
 * the top), and expanded up to 20.
 */
export const LOG_LINES = { shown: 3, wrapped: 8, expanded: 20 } as const;

/** How many lines a log with these settings shows (and is read for). */
export function logLimit(log: { wrap: boolean; open: boolean }): number {
  return log.open ? LOG_LINES.expanded : log.wrap ? LOG_LINES.wrapped : LOG_LINES.shown;
}

/** One of a card's log buttons: its classes, title and icon. */
export type ToggleState = { className: string; title: string; icon: string };

/**
 * The card's expand and wrap buttons. Expand is offered once the box is full (more may be behind
 * it: a collapsed card reads only what it shows); wrap once there's a line. A queued card keeps
 * both slots, hidden, so the icons line up.
 */
export function logToggles(log: LogView | undefined): { expand: ToggleState; wrap: ToggleState } {
  const count = log?.lines?.length ?? 0;
  const open = !!log?.open;
  const wrap = !!log?.wrap;
  return {
    expand: {
      className: `${open ? "on" : ""}${log && (open || count >= LOG_LINES.shown) ? "" : " off"}`,
      title: open
        ? `Show the last ${LOG_LINES.shown} lines`
        : `Show more of the log (up to ${LOG_LINES.expanded} lines)`,
      icon: icon(open ? "collapse" : "expand"),
    },
    wrap: {
      className: `${wrap ? "on" : ""}${log && count ? "" : " off"}`,
      title: wrap ? "Don't wrap long lines" : "Wrap long lines",
      icon: icon("wrap"),
    },
  };
}

const icon = (name: string) => `<svg class="i"><use href="#i-${name}"/></svg>`;

/** The time after the status: running time (or since when), or how long an ended session ran. */
export function timeOf(session: Session, times: TimesAs, now: number): string {
  if (isEnded(session) || times === "relative") return runningTime(session, now);
  return `since ${clock(session.startedAt ?? session.createdAt, now)}`;
}

export function timeTitle(session: Session, times: TimesAs, now: number): string {
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

/**
 * A queued session its provider refused before: which attempt comes next, of how many as things
 * stand (`attempt 2 of 3`; a wait for a usage-limit reset uses no retry, so the total can grow).
 */
function attempt(session: Session): string {
  if (session.status !== "queued" || session.attempts < 1) return "";
  const next = session.attempts + 1;
  return `<span class="attempt">attempt ${next} of ${next + session.retriesLeft}</span>`;
}

/** What a queued session waits for when its provider is paused; the error is its tooltip. */
function waiting(session: Session, health: ProviderHealth | undefined, now: number): string {
  if (session.status !== "queued" || !isPaused(health)) return "";
  const why = session.error ? `Last attempt: ${session.error}` : (health.reason ?? "");
  return `<span class="note paused ${health.status}" title="${escapeHtml(why)}">${escapeHtml(waitingText(health, now))}</span>`;
}

/** The short outcome after the status; the full text is its tooltip. */
export function note(session: Session): string {
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

/** A working session's latest activity, filling its box from the bottom; links in it work. */
export function logBox(log: LogView): string {
  const all = log.lines ?? [];
  const lines = all.slice(-logLimit(log));
  const body = lines.length
    ? lines
        .map(
          (l) =>
            `<div data-kind="${l.kind}" title="${escapeHtml(l.text)}">${linkify(escapeHtml(l.text))}</div>`,
        )
        .join("")
    : log.lines
      ? `<div class="quiet">no output yet</div>`
      : "";
  return `<div class="log${log.open ? " open" : ""}${log.wrap ? " wrap" : ""}">${body}</div>`;
}

/**
 * One session card: status · time · outcome (and, queued, what it waits for), the task, then
 * id · model · caller (· ended); the model by its display names when `providers` has it.
 * `health` is its provider's. A working session's card also shows
 * its log (`log`, M5f) with expand and wrap; a queued one keeps the column (and the icons' slots)
 * empty, so the Overview's cards line up; an ended one has neither.
 */
export function sessionCard(
  session: Session,
  times: TimesAs,
  now: number,
  health?: ProviderHealth,
  log?: LogView,
  providers?: readonly Provider[],
): string {
  const ended = isEnded(session);
  const title = escapeHtml(timeTitle(session, times, now));
  const id = escapeHtml(session.id);
  const r1 = [
    `<span class="badge ${session.status}">${session.status}</span>`,
    `<button class="dur" data-dur="${id}" data-times title="${title}">${timeOf(session, times, now)}</button>`,
    note(session),
    attempt(session),
    waiting(session, health, now),
  ].filter(Boolean);
  const info = [
    `<span class="id" data-copy="${id}" title="Copy the full id (${id})">${shortId(session)}</span>`,
    `<span title="${escapeHtml(`${session.provider}/${session.model}`)}">${escapeHtml(modelLabel(session, providers))}</span>`,
    `<span>${escapeHtml(session.caller)}</span>`,
    ended && session.endedAt
      ? `<button class="when" data-times title="${title}">ended ${
          times === "relative" ? relative(session.endedAt, now) : clock(session.endedAt, now)
        }</button>`
      : "",
  ];
  const working = session.status === "working" && log !== undefined;
  const { expand, wrap } = logToggles(working ? log : undefined);
  const toggles = ended
    ? ""
    : `<button class="${expand.className}" data-expand="${id}" title="${expand.title}" aria-label="Expand the log">${expand.icon}</button>` +
      `<button class="${wrap.className}" data-wrap="${id}" title="${wrap.title}" aria-label="Wrap the log's lines">${wrap.icon}</button>`;
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
      </div>${
        ended
          ? ""
          : `
      <div class="logcol">${working ? logBox(log) : ""}</div>`
      }
      <div class="icons">${toggles}${stop}</div>
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
  .note.paused { color: var(--warn); }
  .note.paused.unauthorized { color: var(--danger); }
  .attempt { color: var(--dim); font-size: 13px; flex: none; }
  .task { font-size: 14px; text-overflow: ellipsis; }
  .info { font: 12px var(--mono); color: var(--dim); }
  .info > span + span::before, .info > span + button::before { content: "·"; margin: 0 6px; color: var(--faint); }
  .info .when { font: inherit; }
  .r1.overflows, .info.overflows { -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 3em), transparent); mask-image: linear-gradient(to right, #000 calc(100% - 3em), transparent); }
  .info .id { cursor: copy; border-bottom: 1px dotted var(--faint); }
  .info .id:hover { color: var(--accent); }

  .icons { display: flex; align-items: center; gap: 1px; align-self: start; padding: 5px 6px 0 0; }
  .icons button { display: inline-grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 6px; background: transparent; color: var(--faint); padding: 0; }
  .icons button:hover { background: var(--hover); color: var(--text); }
  .icons button.on { color: var(--accent); }
  .icons button.stop:hover { background: var(--danger-soft); color: var(--danger); }
  .icons svg.i { width: 15px; height: 15px; }
  .icons .off { visibility: hidden; }
  /* Shown on hover (or keyboard focus), or when switched on; always on touch screens. */
  .icons button { opacity: 0; transition: opacity .12s; }
  .card:hover .icons button, .icons button:focus-visible, .icons button.on, .icons button.stop { opacity: 1; }
  /* Stop (and delete) is always shown, in red. */
  .icons button.stop { color: var(--danger); }
  @media (hover: none) { .icons button { opacity: 1; } }

  /* ---- the log column (M5f): | details | log | icons | on the Overview's cards ---- */
  .card.working, .card.queued { grid-template-columns: minmax(250px, 30%) 1fr auto; }
  .card.working .details, .card.queued .details { border-right: 1px solid var(--line); }
  .logcol { padding: 8px 14px; min-width: 0; position: relative; cursor: text; user-select: text; }
  .log { font: 12.5px var(--mono); color: var(--dim); height: calc(3 * var(--row)); display: flex; flex-direction: column; justify-content: flex-end; overflow: hidden; }
  .log > div { line-height: var(--row); white-space: nowrap; overflow: hidden; flex: none; padding-left: 16px; position: relative; }
  .log > div::before { position: absolute; left: 0; top: 0; width: 12px; text-align: center; }
  .log [data-kind="text"] { color: var(--text); } .log [data-kind="text"]::before { content: "›"; color: var(--faint); }
  .log [data-kind="tool"] { color: var(--accent); }
  .log [data-kind="tool"]::before { content: ""; top: 5px; height: 12px; background: currentColor;
    -webkit-mask: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z'/%3E%3C/svg%3E") center / contain no-repeat;
            mask: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z'/%3E%3C/svg%3E") center / contain no-repeat; }
  .log a { color: var(--accent); }
  .log [data-kind="error"] { color: var(--failed); } .log [data-kind="error"]::before { content: "!"; font-weight: 700; }
  .log .quiet { color: var(--faint); font: italic 13px system-ui, sans-serif; padding-left: 0; }
  .log .quiet::before { content: none; }
  /* Unwrapped lines that don't fit fade at the right edge (.overflows is measured after render). */
  .log > div.overflows { -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 4em), transparent); mask-image: linear-gradient(to right, #000 calc(100% - 4em), transparent); }
  /* Wrap only changes how lines break; the box keeps its height and older lines leave at the top. */
  .log.wrap > div { white-space: pre-wrap; overflow-wrap: anywhere; }
  .log.wrap > div.overflows { -webkit-mask-image: none; mask-image: none; }
  .log.wrap.clipped:not(.open) { -webkit-mask-image: linear-gradient(to bottom, transparent, #000 1.2em); mask-image: linear-gradient(to bottom, transparent, #000 1.2em); }
  /* Expand is what makes it grow. */
  .log.open { height: auto; min-height: calc(3 * var(--row)); }

  @media (max-width: 720px) {
    .card.working, .card.queued { grid-template-columns: 1fr auto; }
    .card.working .details, .card.queued .details { border-right: 0; grid-column: 1; grid-row: 1; }
    .icons { grid-column: 2; grid-row: 1; }
    .logcol { grid-column: 1 / -1; border-top: 1px solid var(--line); }
    .card.queued .logcol { display: none; }
  }

  .empty { color: var(--dim); text-align: center; padding: 34px; }
  .error { margin: 0 0 10px; padding: 8px 12px; border-radius: 8px; background: var(--danger-soft); color: var(--danger); font-size: 13px; }
  .footer-note { text-align: center; color: var(--dim); font-size: 13px; margin-top: 6px; }
  .footer-note button { border: 0; background: none; color: var(--accent); padding: 0; }
`;
