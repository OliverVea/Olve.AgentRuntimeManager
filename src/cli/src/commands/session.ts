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
import { commaList, dateTime, flag, integer, oneOf, pairs, text } from "../options";
import { formatKeyValue, formatTable, localDateTime, truncate } from "../output";
import type { CommandGroup, OptionValues } from "../registry";

/** Every session status (typed, so a status added to the contract fails to compile here). */
const statusSet: Record<SessionStatus, true> = {
  queued: true,
  working: true,
  waiting: true,
  completed: true,
  killed: true,
  failed: true,
};
export const sessionStatuses = Object.keys(statusSet) as SessionStatus[];

const idArg = { name: "id", description: "Session ID (a UUID)" };

function joinPairs(map: Record<string, unknown> | undefined, separator: string): string | undefined {
  const entries = Object.entries(map ?? {});
  return entries.length ? entries.map(([k, v]) => `${k}${separator}${String(v)}`).join(", ") : undefined;
}

function joinList(items: readonly string[] | undefined): string | undefined {
  return items?.length ? items.join(", ") : undefined;
}

/** Key/value lines for a session; unset and empty fields are left out. */
export function formatSession(s: Session): string {
  const entries: Array<readonly [string, unknown]> = [
    ["id", s.id],
    ["status", s.status === "queued" && s.queuePosition !== undefined ? `queued (position ${s.queuePosition})` : s.status],
    ["prompt", s.prompt],
    ["provider", s.provider],
    ["model", s.model],
    ["effort", s.effort],
    ["systemPrompt", s.systemPrompt],
    ["caller", s.caller],
    ["tags", joinPairs(s.tags, ":")],
    ["env", joinPairs(s.env, "=")],
    ["timeoutSeconds", s.timeoutSeconds],
    ["approvalPolicy", s.approvalPolicy],
    ["tools", joinList(s.tools)],
    ["skills", joinList(s.skills)],
    ["messaging", s.messaging],
    ["headless", s.headless],
    ["createdAt", s.createdAt],
    ["startedAt", s.startedAt],
    ["endedAt", s.endedAt],
    ["providerSessionId", s.providerSessionId],
    ["exitCode", s.exitCode],
    ["summary", s.summary],
    ["error", s.error],
    ["killReason", s.killReason],
    ["killSource", s.killSource],
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

function createBody(options: OptionValues): CreateSession {
  const prompt = options.prompt;
  if (prompt === undefined) throw new UsageError("missing required option --prompt (-p)");
  if (prompt === "") throw new UsageError("--prompt must not be empty");
  if (options.messaging === true && options["no-messaging"] === true) {
    throw new UsageError("--messaging and --no-messaging are mutually exclusive");
  }
  const body: CreateSession = {
    prompt: String(prompt),
    provider: text(options, "provider"),
    model: text(options, "model"),
    effort: text(options, "effort"),
    systemPrompt: text(options, "system-prompt"),
    caller: text(options, "caller"),
    tags: pairs(options, "tag", ":"),
    env: pairs(options, "env", "="),
    secretEnv: pairs(options, "secret-env", "="),
    timeoutSeconds: integer(options, "timeout-seconds", { min: 1 }),
    approvalPolicy: text(options, "policy"),
    tools: commaList(options, "tools"),
    skills: commaList(options, "skills"),
    messaging: options.messaging === true ? true : options["no-messaging"] === true ? false : undefined,
    headless: flag(options, "headless"),
  };
  // Leave unset fields out of the request, so the server applies its defaults.
  return Object.fromEntries(Object.entries(body).filter(([, v]) => v !== undefined)) as CreateSession;
}

function searchBody(options: OptionValues): SessionSearch {
  const body: SessionSearch = {
    status: oneOf(options, "status", sessionStatuses),
    caller: text(options, "caller"),
    tags: pairs(options, "tag", ":"),
    createdAfter: dateTime(options, "after"),
    createdBefore: dateTime(options, "before"),
    text: text(options, "text"),
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
      args: [],
      options: {
        prompt: { type: "string", short: "p", description: "What the agent should do (required)", valueName: "TEXT" },
        provider: { type: "string", description: "Provider that runs the agent (default: the server's)", valueName: "X" },
        model: { type: "string", description: "Model", valueName: "X" },
        effort: { type: "string", description: "Effort level", valueName: "X" },
        "system-prompt": { type: "string", description: "An additional system prompt", valueName: "TEXT" },
        caller: { type: "string", description: "Who started the session (e.g. oribot); searchable", valueName: "X" },
        tag: { type: "string", multiple: true, description: "A tag", valueName: "K:V" },
        "timeout-seconds": {
          type: "string",
          description: "Kill the session after this long (default: the server's)",
          valueName: "N",
        },
        headless: { type: "boolean", description: "Auto-deny anything the policy doesn't allow (CI, scripting)" },
        messaging: { type: "boolean", description: "Enable the messages endpoint (the default)" },
        "no-messaging": { type: "boolean", description: "Disable the messages endpoint" },
        env: { type: "string", multiple: true, description: "An environment variable for the agent", valueName: "K=V" },
        "secret-env": {
          type: "string",
          multiple: true,
          description: "A secret env var, only for approved_bash commands; never returned",
          valueName: "K=V",
        },
        policy: { type: "string", description: "Approval policy", valueName: "X" },
        tools: { type: "string", description: "Tools, comma-separated", valueName: "T1,T2" },
        skills: { type: "string", description: "Skills, comma-separated", valueName: "S1,S2" },
        "idempotency-key": {
          type: "string",
          description: "Repeating a key within 24h returns the original session instead of creating another",
          valueName: "KEY",
        },
      },
      async run({ options, client }) {
        const body = createBody(options);
        const key = text(options, "idempotency-key");
        const { data: session, status } = await send(
          sessionsCreate({ client, body, ...(key ? { headers: { "Idempotency-Key": key } } : {}) }),
          client,
        );
        const headline =
          status === 202
            ? `Queued session ${session.id}${session.queuePosition !== undefined ? ` at position ${session.queuePosition}` : ""}.`
            : `Started session ${session.id}.`;
        return { json: session, pretty: `${headline}\n\n${formatSession(session)}` };
      },
    },
    {
      name: "list",
      summary: "Search sessions, newest first, one page at a time",
      args: [],
      options: {
        status: { type: "string", description: `Only this status: ${sessionStatuses.join(", ")}`, valueName: "X" },
        caller: { type: "string", description: "Only sessions started by this caller", valueName: "X" },
        tag: { type: "string", multiple: true, description: "Only sessions with this tag", valueName: "K:V" },
        after: { type: "string", description: "Created after this date or date-time", valueName: "DATE" },
        before: { type: "string", description: "Created before this date or date-time", valueName: "DATE" },
        text: { type: "string", description: "Prompt contains this (case-insensitive)", valueName: "X" },
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
      summary: "Kill a queued, working or waiting session (one that already ended is a 409, exit 4)",
      args: [idArg],
      options: {
        reason: { type: "string", description: "Why (recorded on the session and its event)", valueName: "TEXT" },
      },
      async run({ args, options, client }) {
        const reason = text(options, "reason");
        const session = await unwrap(
          sessionsKill({ client, path: { id: args.id! }, body: reason ? { reason } : {} }),
          client,
        );
        return { json: session, pretty: `Killed session ${session.id}.\n\n${formatSession(session)}` };
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
