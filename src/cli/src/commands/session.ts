import {
  sessionsCreate,
  sessionsDelete,
  sessionsGet,
  sessionsKill,
  sessionsSearch,
  type CreateSession,
  type Session,
  type SessionPage,
  type SessionSearch,
  type SessionStatus,
} from "@arm/client";
import { send, unwrap } from "../api";
import { UsageError } from "../errors";
import { commaListOf, dateTime, integer, text, textOrDefault } from "../options";
import { formatKeyValue, formatTable, localDateTime, truncate } from "../output";
import type { CommandContext, CommandGroup, OptionValues } from "../registry";

/** Every session status (typed, so a status added to the contract fails to compile here). */
const statusSet: Record<SessionStatus, true> = {
  queued: true,
  working: true,
  completed: true,
  cancelled: true,
  killed: true,
  failed: true,
};
export const sessionStatuses = Object.keys(statusSet) as SessionStatus[];

const idArg = { name: "id", description: "Session ID (a UUID)" };

/** Key/value lines for a session; unset and empty fields are left out. */
export function formatSession(s: Session): string {
  const entries: Array<readonly [string, unknown]> = [
    ["id", s.id],
    ["status", s.status === "queued" && s.queuePosition !== undefined ? `queued (position ${s.queuePosition})` : s.status],
    ["prompt", s.prompt],
    ["provider", s.provider],
    ["model", s.model],
    ["caller", s.caller],
    ["timeoutSeconds", s.timeoutSeconds],
    ["attempts", s.attempts],
    ["createdAt", s.createdAt],
    ["startedAt", s.startedAt],
    ["endedAt", s.endedAt],
    ["providerSessionId", s.providerSessionId],
    ["exitCode", s.exitCode],
    ["error", s.error],
    ["killReason", s.killReason],
    ["killSource", s.killSource],
    ["killCaller", s.killCaller],
  ];
  return formatKeyValue(entries.filter(([, v]) => v !== undefined && v !== null && v !== ""));
}

/** A table of the page, then `Showing 1–20 of 57 sessions · next: --offset 20`. */
export function formatSessionPage(page: SessionPage): string {
  const plural = (n: number) => `${n} session${n === 1 ? "" : "s"}`;
  if (page.items.length === 0) {
    return page.total > 0 && page.offset > 0
      ? `No sessions at offset ${page.offset} (${plural(page.total)} in total).`
      : "No sessions.";
  }
  const table = formatTable(page.items, [
    { header: "id", value: (s: Session) => s.id },
    { header: "status", value: (s: Session) => s.status },
    { header: "provider", value: (s: Session) => s.provider },
    { header: "caller", value: (s: Session) => s.caller },
    { header: "created", value: (s: Session) => localDateTime(s.createdAt) },
    { header: "prompt", value: (s: Session) => truncate(s.prompt, 60) },
  ]);
  const first = page.offset + 1;
  const last = page.offset + page.items.length;
  let footer = `Showing ${first}–${last} of ${plural(page.total)}`;
  if (last < page.total) footer += ` · next: --offset ${last}`;
  return `${table}\n\n${footer}`;
}

/** Built-in defaults until the server lists its providers (M4b); `arm config set provider|model …` overrides them. */
export const DEFAULT_PROVIDER = "claude";
export const DEFAULT_MODEL = "sonnet";

function createBody(prompt: string, options: OptionValues, ctx: Pick<CommandContext, "settings" | "user">): CreateSession {
  if (prompt.trim() === "") throw new UsageError("<prompt> must not be empty");
  const body: CreateSession = {
    prompt,
    provider: textOrDefault(options, "provider", ctx.settings.provider ?? DEFAULT_PROVIDER),
    model: textOrDefault(options, "model", ctx.settings.model ?? DEFAULT_MODEL),
    caller: textOrDefault(options, "caller", ctx.settings.caller ?? ctx.user),
    timeoutSeconds: integer(options, "timeout-seconds", { min: 1 }),
  };
  // Leave unset fields out of the request, so the server applies its defaults.
  return Object.fromEntries(Object.entries(body).filter(([, v]) => v !== undefined)) as CreateSession;
}

function searchBody(options: OptionValues): SessionSearch {
  const body: SessionSearch = {
    status: commaListOf(options, "status", sessionStatuses),
    caller: text(options, "caller"),
    createdAfter: dateTime(options, "after"),
    createdBefore: dateTime(options, "before"),
    limit: integer(options, "limit", { min: 1, max: 100 }),
    offset: integer(options, "offset", { min: 0 }),
  };
  return Object.fromEntries(Object.entries(body).filter(([, v]) => v !== undefined)) as SessionSearch;
}

export const sessionGroup: CommandGroup = {
  name: "session",
  summary: "Create, find and manage agent sessions",
  commands: [
    {
      name: "create",
      summary: "Create a session: it starts right away when a slot is free, else it is queued",
      args: [{ name: "prompt", description: "What the agent should do (quote it)" }],
      options: {
        provider: { type: "string", description: "Provider that runs the agent (default: setting provider, else claude)", valueName: "X" },
        model: { type: "string", description: "Model, in the provider's own naming (default: setting model, else sonnet)", valueName: "X" },
        caller: { type: "string", description: "Who started the session; searchable (default: setting caller, else your user name)", valueName: "X" },
        "timeout-seconds": {
          type: "string",
          description: "Kill the session this long after it starts (default: no timeout)",
          valueName: "N",
        },
        "idempotency-key": {
          type: "string",
          description: "Repeating a key within 24h returns the original session instead of creating another",
          valueName: "KEY",
        },
      },
      async run({ args, options, client, settings, user }) {
        const body = createBody(args.prompt!, options, { settings, user });
        const key = text(options, "idempotency-key");
        const { data: created, status } = await send(
          sessionsCreate({ client, body, ...(key ? { headers: { "Idempotency-Key": key } } : {}) }),
          client,
        );
        // Only the id comes back; `arm session get <id>` shows the session.
        return { json: created, pretty: `${status === 202 ? "Queued" : "Started"} session ${created.id}.` };
      },
    },
    {
      name: "list",
      summary: "Search sessions, newest first, one page at a time",
      args: [],
      options: {
        status: { type: "string", description: `Only these statuses, comma-separated: ${sessionStatuses.join(", ")}`, valueName: "X,Y" },
        caller: { type: "string", description: "Only sessions started by this caller", valueName: "X" },
        after: { type: "string", description: "Created after this date or date-time", valueName: "DATE" },
        before: { type: "string", description: "Created before this date or date-time", valueName: "DATE" },
        limit: { type: "string", description: "Page size, 1–100 (default 20)", valueName: "N" },
        offset: { type: "string", description: "Skip this many matches (default 0)", valueName: "N" },
      },
      async run({ options, client }) {
        const page = await unwrap(sessionsSearch({ client, body: searchBody(options) }), client);
        return { json: page, pretty: formatSessionPage(page) };
      },
    },
    {
      name: "get",
      summary: "Show one session",
      args: [idArg],
      options: {},
      async run({ args, client }) {
        const session = await unwrap(sessionsGet({ client, path: { id: args.id! } }), client);
        return { json: session, pretty: formatSession(session) };
      },
    },
    {
      name: "kill",
      summary: "Kill a queued or working session (one that already ended is a 409, exit 4)",
      args: [idArg],
      options: {
        caller: { type: "string", description: "Who is killing the session (default: setting caller, else your user name)", valueName: "X" },
        reason: { type: "string", description: "Why (recorded on the session and its event)", valueName: "TEXT" },
      },
      async run({ args, options, client, settings, user }) {
        const caller = textOrDefault(options, "caller", settings.caller ?? user);
        const reason = text(options, "reason");
        const session = await unwrap(
          sessionsKill({ client, path: { id: args.id! }, body: reason ? { caller, reason } : { caller } }),
          client,
        );
        // A queued session is cancelled, a working one killed; the status says which.
        const verb = session.status === "cancelled" ? "Cancelled" : "Killed";
        return { json: session, pretty: `${verb} session ${session.id}.\n\n${formatSession(session)}` };
      },
    },
    {
      name: "delete",
      summary: "Delete a session that has ended (one that hasn't is a 409, exit 4)",
      args: [idArg],
      options: {},
      async run({ args, client }) {
        await unwrap(sessionsDelete({ client, path: { id: args.id! } }), client);
        return { json: {}, pretty: `Deleted session ${args.id}.` };
      },
    },
  ],
};
