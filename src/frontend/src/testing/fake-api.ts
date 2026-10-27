import type {
  ActivityLine,
  ArmEventData,
  EnvVariable,
  Provider,
  ProviderHealth,
  Session,
  SessionSearch,
  Skill,
  Tool,
} from "@arm/client";
import { vi } from "vitest";
import { createApiClient } from "../api-client.js";

export function session(id: string, overrides: Partial<Session> = {}): Session {
  return {
    id,
    status: "working",
    prompt: `prompt of ${id}`,
    provider: "fake",
    model: "fake",
    caller: "tester",
    // A session starts on its first attempt; a fresh queued one hasn't yet.
    attempts: overrides.status === "queued" ? 0 : 1,
    retriesLeft: 2,
    createdAt: "2026-09-26T10:00:00Z",
    tools: ["arm_bash", "arm_file_read"],
    skills: [],
    ...overrides,
  };
}

/** What `GET /api/skills` answers when asked for a list: two of them default, one not. */
export const defaultSkillList: Skill[] = [
  {
    name: "arm-conventions",
    description: "ARM's own conventions.",
    default: true,
    source: { url: "git@github.com:OliverVea/Olve.AgentRuntimeManager.git", path: "docs/skills" },
    updatedAt: "2026-10-25T12:00:00Z",
  },
  {
    name: "pdf",
    description: "PDF manipulation.",
    default: false,
    source: { url: "https://github.com/anthropics/skills", path: "skills/pdf" },
    updatedAt: "2026-10-27T09:00:00Z",
  },
  {
    name: "pipelines",
    description: "Working with Olve.Pipelines.",
    default: true,
    source: { url: "git@github.com:OliverVea/ovea-skills.git", ref: "main", path: "skills/pl" },
    updatedAt: "2026-10-27T09:00:00Z",
  },
];

/** What `GET /api/providers` answers by default. */
export const defaultModels: Provider[] = [
  {
    name: "claude",
    displayName: "Claude Code",
    models: [
      { id: "claude-opus-5-5", displayName: "Opus 5.5" },
      { id: "claude-sonnet-5", displayName: "Sonnet 5" },
    ],
  },
  { name: "fake", displayName: "fake", models: [{ id: "fake", displayName: "Instant" }] },
];

/** What `GET /api/tools` answers by default: four of the server's defaults, and one not. */
export const defaultToolList: Tool[] = [
  { name: "arm_bash", description: "Run a shell command.", default: true },
  { name: "arm_file_read", description: "Read a text file.", default: true },
  { name: "arm_file_edit", description: "Replace an exact string in a file.", default: true },
  { name: "arm_file_create", description: "Create a new file.", default: true },
  { name: "arm_file_delete", description: "Delete a file.", default: false },
];

export type Call = { method: string; path: string; search: string; body: unknown };

/**
 * A real generated client over a scripted `fetch`: the code under test drives the Hey API SDK,
 * and this fake backend answers by method + path, recording every request. Search filters
 * `sessions` by status and pages them newest first. Each `GET /api/events` opens a stream that
 * stays open; `push` writes one SSE frame to the latest, `endStream` ends it like a redeploy.
 * `holdSearch` makes searches wait for `releaseSearch`, to deliver events before the snapshot.
 * `providers` is what `GET /api/providers/health` answers (every provider available by default),
 * `models` what `GET /api/providers` answers (Claude Code's Opus and Sonnet and fake's Instant by
 * default; a status: that error instead), `tools` what `GET /api/tools` answers (`defaultToolList`
 * by default; a status: that error instead).
 * `env` is the registered variables `/api/env` lists, sets and deletes; `failEnv` makes the next
 * call to `/api/env` answer with the given error envelope instead; `holdEnvWrites` makes PUTs and
 * DELETEs wait for `releaseEnvWrites`. `skills` is the registered skills `/api/skills` lists,
 * installs (a PUT keeps the description of one that exists) and deletes, with `failSkills` and
 * `holdSkillWrites` / `releaseSkillWrites` alike. `activity` is what `GET /api/sessions/{id}/activity`
 * answers for a session (its lines; 404 when it gives none; `"hang"`: no answer until the request
 * is aborted, counted by `aborted()`), read when asked.
 */
export function fakeApi(
  options: {
    sessions?: Session[];
    search?: () => Response;
    providers?: ProviderHealth[];
    models?: Provider[] | number;
    tools?: Tool[] | number;
    env?: EnvVariable[];
    skills?: Skill[];
    activity?: (id: string) => ActivityLine[] | undefined | "hang";
  } = {},
) {
  const calls: Call[] = [];
  const sessions = options.sessions ?? [];
  const env = options.env ?? [];
  const skills = options.skills ?? [];
  let envFailure: Response | undefined;
  let heldWrites: Array<() => void> | undefined;
  let skillFailure: Response | undefined;
  let heldSkillWrites: Array<() => void> | undefined;
  const encoder = new TextEncoder();
  let events: ReadableStreamDefaultController<Uint8Array> | undefined;
  let nextId = 1;
  let held: Array<() => void> | undefined;
  let aborted = 0;

  const searchResponse = (body: SessionSearch) => {
    if (options.search) return options.search();
    const matches = sessions
      .filter((s) => !body.status || body.status.includes(s.status))
      .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
    const offset = body.offset ?? 0;
    const limit = body.limit ?? 20;
    return Response.json({
      items: matches.slice(offset, offset + limit),
      total: matches.length,
      limit,
      offset,
    });
  };

  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    const body = text ? JSON.parse(text) : undefined;
    calls.push({ method: request.method, path: url.pathname, search: url.search, body });

    if (request.method === "POST" && url.pathname === "/api/sessions/search") {
      if (held) await new Promise<void>((resolve) => held?.push(resolve));
      return searchResponse(body ?? {});
    }
    const activityOf = url.pathname.match(/^\/api\/sessions\/([^/]+)\/activity$/);
    if (request.method === "GET" && activityOf) {
      const lines = options.activity?.(activityOf[1] ?? "");
      if (lines === "hang") {
        return new Promise<Response>((_, reject) => {
          const abort = () => {
            aborted++;
            reject(new DOMException("aborted", "AbortError"));
          };
          if (request.signal.aborted) abort();
          else request.signal.addEventListener("abort", abort);
        });
      }
      return lines
        ? Response.json({ lines })
        : Response.json(
            { error: { code: "SESSION_NOT_FOUND", message: "Not found.", details: {} } },
            { status: 404 },
          );
    }
    const byId = url.pathname.match(/^\/api\/sessions\/([^/]+)$/);
    if (request.method === "GET" && byId) {
      const found = sessions.find((s) => s.id === byId[1]);
      return found ? Response.json(found) : new Response(null, { status: 404 });
    }
    if (request.method === "GET" && url.pathname === "/api/providers") {
      const models = options.models ?? defaultModels;
      return typeof models === "number"
        ? Response.json(
            { error: { code: "INTERNAL", message: "The server failed.", details: {} } },
            { status: models },
          )
        : Response.json(models);
    }
    if (request.method === "GET" && url.pathname === "/api/tools") {
      const tools = options.tools ?? defaultToolList;
      return typeof tools === "number"
        ? Response.json(
            { error: { code: "INTERNAL", message: "The server failed.", details: {} } },
            { status: tools },
          )
        : Response.json(tools);
    }
    if (request.method === "GET" && url.pathname === "/api/providers/health") {
      return Response.json(
        options.providers ?? [
          { provider: "claude", status: "available" },
          { provider: "fake", status: "available" },
        ],
      );
    }
    if (url.pathname === "/api/env" || url.pathname.startsWith("/api/env/")) {
      if (heldWrites && request.method !== "GET") {
        await new Promise<void>((resolve) => heldWrites?.push(resolve));
      }
      const failure = envFailure;
      envFailure = undefined;
      if (failure) return failure;
      const name = decodeURIComponent(url.pathname.slice("/api/env/".length));
      const at = env.findIndex((v) => v.name === name);
      if (request.method === "GET") return Response.json(env);
      if (request.method === "PUT") {
        const variable = { name, ...body, updatedAt: "2026-09-27T12:00:00Z" } as EnvVariable;
        if (at >= 0) env[at] = variable;
        else env.push(variable);
        return Response.json(variable);
      }
      if (request.method === "DELETE" && at >= 0) {
        env.splice(at, 1);
        return new Response(null, { status: 204 });
      }
      return Response.json(
        {
          error: {
            code: "ENV_NOT_FOUND",
            message: `No environment variable '${name}' is registered.`,
          },
        },
        { status: 404 },
      );
    }
    if (url.pathname === "/api/skills" || url.pathname.startsWith("/api/skills/")) {
      if (heldSkillWrites && request.method !== "GET") {
        await new Promise<void>((resolve) => heldSkillWrites?.push(resolve));
      }
      const failure = skillFailure;
      skillFailure = undefined;
      if (failure) return failure;
      const name = decodeURIComponent(url.pathname.slice("/api/skills/".length));
      const at = skills.findIndex((s) => s.name === name);
      if (request.method === "GET") return Response.json(skills);
      if (request.method === "PUT") {
        const skill: Skill = {
          name,
          description: skills[at]?.description ?? `The ${name} skill.`,
          default: body.default ?? false,
          source: body.source,
          updatedAt: "2026-10-27T12:00:00Z",
        };
        if (at >= 0) skills[at] = skill;
        else skills.push(skill);
        return Response.json(skill);
      }
      if (request.method === "DELETE" && at >= 0) {
        skills.splice(at, 1);
        return new Response(null, { status: 204 });
      }
      return Response.json(
        { error: { code: "SKILL_NOT_FOUND", message: `No skill '${name}' is registered.` } },
        { status: 404 },
      );
    }
    if (request.method === "GET" && url.pathname === "/api/events") {
      const stream = new ReadableStream<Uint8Array>({
        start: (controller) => {
          events = controller;
        },
      });
      return new Response(stream, { headers: { "Content-Type": "text/event-stream" } });
    }
    return new Response(null, { status: 404 });
  });

  return {
    client: createApiClient("http://test", { fetch }),
    calls,
    sessions,
    env,
    envCalls: () => calls.filter((c) => c.path.startsWith("/api/env")),
    failEnv(status: number, code: string, message: string) {
      envFailure = Response.json({ error: { code, message, details: {} } }, { status });
    },
    holdEnvWrites() {
      heldWrites = [];
    },
    releaseEnvWrites() {
      const waiting = heldWrites ?? [];
      heldWrites = undefined;
      for (const resolve of waiting) resolve();
    },
    skills,
    skillCalls: () => calls.filter((c) => c.path.startsWith("/api/skills")),
    failSkills(status: number, code: string, message: string) {
      skillFailure = Response.json({ error: { code, message, details: {} } }, { status });
    },
    holdSkillWrites() {
      heldSkillWrites = [];
    },
    releaseSkillWrites() {
      const waiting = heldSkillWrites ?? [];
      heldSkillWrites = undefined;
      for (const resolve of waiting) resolve();
    },
    streams: () => calls.filter((c) => c.path === "/api/events").length,
    searches: () => calls.filter((c) => c.path === "/api/sessions/search"),
    /** How many hung activity reads were aborted. */
    aborted: () => aborted,
    /** The activity reads, of one session or of all. */
    activityReads: (id?: string) =>
      calls.filter((c) =>
        id ? c.path === `/api/sessions/${id}/activity` : c.path.endsWith("/activity"),
      ),
    push(event: ArmEventData) {
      if (!events) throw new Error("event stream not open");
      const id = event.type === "heartbeat" ? "" : `id: ${nextId++}\n`;
      events.enqueue(
        encoder.encode(`event: ${event.type}\n${id}data: ${JSON.stringify(event)}\n\n`),
      );
    },
    endStream() {
      events?.close();
      events = undefined;
    },
    holdSearch() {
      held = [];
    },
    releaseSearch() {
      const waiting = held ?? [];
      held = undefined;
      for (const resolve of waiting) resolve();
    },
  };
}
