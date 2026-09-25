# ARM — V0 Spec

ARM (Agent Runtime Manager) is a standalone agent runtime. It runs and queues LLM agent sessions,
streams everything they do as events (SSE), gates their system access through human approvals,
and manages the skills and tools they get. It is provider-agnostic (Claude Code first, then Codex)
and frontend-agnostic: the CLI, web UI, Slack and custom integrations are all equal API clients.
ARM has no opinion on what agents do.

This spec covers V0 at user/API level. Later ideas: [`VISION.md`](VISION.md). Order of work:
[`MILESTONES.md`](MILESTONES.md).

---

## API conventions

| Method | Use |
|---|---|
| `GET` | Retrieve one resource by ID |
| `QUERY` | Search/list; the request body carries the filter (RFC 10008) |
| `POST` | Create a resource or trigger an action |
| `PATCH` | Partial update |
| `DELETE` | Remove a resource |

`queryMethod` (`query` \| `get` \| `post`, default `query`) switches search endpoints to
query-string `GET` or `POST /api/<resource>/search` for infrastructure without `QUERY`.

**Errors** always use one envelope:

```json
{ "error": { "code": "APPROVAL_EXPIRED", "message": "Approval arm-apv-xyz expired after 30 minutes", "details": {} } }
```

| Status | Meaning |
|---|---|
| 400 | Malformed request / invalid parameters |
| 401 | Missing or invalid token |
| 403 | Not permitted (e.g. messaging disabled) |
| 404 | Not found |
| 409 | Conflict (already-resolved approval, already-dead session, message to a queued session, revive a running session) |
| 410 | Existed but purged by retention |
| 422 | Semantically invalid (e.g. completion output fails its schema) |
| 429 | Rate limited |
| 503 | Queue full, or server draining |

When two frontends decide the same approval, the first wins (200) and the second gets 409.

**Idempotency:** `create` and `revive` accept an `Idempotency-Key` header (`--idempotency-key`);
a repeated key within 24 hours returns the original response.

---

## Auth

- `arm login` issues a signed token. With no auth backend configured, ARM issues tokens itself
  (single-user, zero config); otherwise it validates tokens from the configured backend.
- **Permissions** are fine-grained (`sessions:create`, `sessions:kill`, `approvals:decide`, …)
  and composed into roles. Default roles: read-only, operator (start, kill, approve; no
  configuration), admin.
- **Agent tokens** (`AGENT_RUNTIME_TOKEN`) are limited to their own session (approvals, messages,
  skill loads). Agents can't approve their own requests, and their subprocesses can't read the
  token.
- The `actor` on an approval decision comes from the token, never the payload.

---

## Sessions

| CLI | API | Purpose |
|---|---|---|
| `arm session create -p "prompt" [--provider X] [--model X] [--effort X] [--caller X] [--tag k:v] [--timeout-seconds N] [--headless] [--messaging\|--no-messaging] [--env K=V] [--secret-env K=V] [--policy X] [--tools T1,T2] [--skills S1,S2]` | `POST /api/sessions` | Create (starts, or queues: 202 + position) |
| `arm session list [--status X] [--caller X] [--tag k:v] [--after DATE] [--before DATE] [--limit N] [--offset N]` | `QUERY /api/sessions` | Search |
| `arm session get <id>` | `GET /api/sessions/:id` | Get |
| `arm session kill <id> [--reason "..."]` | `POST /api/sessions/:id/kill` | Kill a queued/working/waiting session |
| `arm session delete <id>` | `DELETE /api/sessions/:id` | Delete (terminal sessions only) |
| `arm session revive <id> [-p "follow-up"] [--tools …] [--skills …]` | `POST /api/sessions/:id/revive` | New session continuing this one |
| `arm session logs <id> [-f] [--since T] [--until T]` | `GET /api/sessions/:id/logs` | Log (streams with `-f`) |
| `arm session conversation <id> [--format json\|text]` | `GET /api/sessions/:id/conversation` | Structured conversation |
| `arm session message <id> "text"` | `POST /api/sessions/:id/messages` | Message the agent |

**Create payload** (all agent configuration is inline):

```json
{
  "prompt": "...",
  "provider": "claude",
  "model": "claude-sonnet-4-6[1m]",
  "effort": "high",
  "systemPrompt": "Optional additional system prompt.",
  "caller": "oribot",
  "tags": {},
  "env": {},
  "secretEnv": {},
  "timeoutSeconds": 600,
  "approvalPolicy": "standard",
  "tools": ["arm-approved-bash", "arm-file-ops", "arm-skills", "arm-messaging"],
  "skills": ["brazil", "git-workflows"],
  "messaging": true,
  "headless": false
}
```

**Search body** (all fields optional; empty = everything):

```json
{ "status": "working", "caller": "oribot", "tags": {"team": "seller-growth"},
  "createdAfter": "2026-08-01T00:00:00Z", "createdBefore": "2026-08-10T00:00:00Z",
  "text": "deploy", "limit": 20, "offset": 0 }
```

**Status:** `queued → working → completed`; `working ⇄ waiting` (waiting = pending approval);
`queued|working|waiting → killed`; `queued|working → failed`. Terminal: `completed`, `killed`,
`failed`.

- **`secretEnv`** is write-only: never returned, never logged or persisted, and only passed to
  commands run through `approved_bash`.
- **Messaging** is on by default; with `messaging: false` the messages endpoint returns 403.
- **Headless** sessions auto-deny anything the policy doesn't `allow` (for CI/scripting).
- **Revive** creates a new, independent session seeded with the parent's conversation, using its
  own tools/skills/policy (default: the parent's). The parent stays terminal and gains
  `childSessionId`. Reviving the same parent twice gives two independent sessions.

---

## Approvals

The agent's only access to the system (shell, files, messaging) is through ARM's tools. Each call
is checked against the session's policy:

- **safe** → runs immediately, output returned
- **blocked** → error with reason
- **needs approval** → ARM emits `session.approval` and the call waits for a human decision
  (auto-deny after a configurable timeout)

The check understands shell syntax (pipes, chains, subshells, quoting) and fails closed: anything
it can't parse needs approval.

| CLI | API | Purpose |
|---|---|---|
| `arm approval list [--session X] [--status pending\|approved\|denied\|expired] [--kind bash\|file\|tool] [--after DATE] [--limit N]` | `QUERY /api/approvals` | Search |
| `arm approval get <session-id> <approval-id>` | `GET /api/sessions/:id/approvals/:aid` | Get |
| `arm approval approve\|deny <session-id> <approval-id> [--reason "..."]` | `POST /api/sessions/:id/approvals/:aid/decide` | Decide: `{ "decision": "approve" \| "deny", "reason": "…" }` |

### Agent tools

Enabled per session via `tools`; none are on by default (no tools = reasoning only).

| Module | Tools |
|---|---|
| `arm-approved-bash` | `approved_bash` |
| `arm-file-ops` | `approved_file_read`, `approved_file_edit`, `approved_file_create`, `approved_file_delete` |
| `arm-skills` | `skill_list`, `skill_load` |
| `arm-messaging` | `message_user`, `check_user_replies` |

```json
// approved_bash call
{ "command": "git push origin main", "purpose": "Deploy fix", "working_dir": "/path/to/repo" }
// blocked
{ "error": "BLOCKED", "reason": "rm commands are not allowed in this policy" }
// denied by a human (or timed out)
{ "error": "DENIED", "reason": "Not now; run the tests first", "actor": "oliver" }
```

### Policies

Versioned: every update creates a new version; unreferenced old versions are cleaned up. Rules are
evaluated in order, first match wins, and unmatched commands need approval. Path rules let a
policy allow reads broadly while restricting writes.

| CLI | API | Purpose |
|---|---|---|
| `arm policy list [--text "..."]` | `QUERY /api/policies` | List |
| `arm policy get <name> [--version N]` | `GET /api/policies/:name` | Get (latest or a version) |
| `arm policy create <name> [--from <file>]` | `POST /api/policies` | Create |
| `arm policy update <name> [--from <file>]` | `PATCH /api/policies/:name` | Update (new version) |
| `arm policy delete <name>` | `DELETE /api/policies/:name` | Delete (fails while referenced) |
| `arm policy test <name> [--file commands.txt]` | — | Check commands against a policy (stdin if no file) |
| `arm policy history <name> [--limit N]` | — | Version history |

```json
{
  "name": "standard",
  "version": 3,
  "rules": [
    {"action": "allow", "pattern": "git log *", "kind": "bash"},
    {"action": "deny", "pattern": "git push --force *", "kind": "bash", "reason": "Force push is never safe."},
    {"action": "allow", "pattern": "file-read", "kind": "file", "paths": ["**"]},
    {"action": "allow", "pattern": "file-edit", "kind": "file", "paths": ["/tmp/**", "./src/**"]},
    {"action": "deny", "pattern": "file-edit", "kind": "file", "paths": ["/etc/**", "/home/*/.ssh/**"], "reason": "System files are off limits."}
  ]
}
```

---

## Skills & tools

| CLI | API | Purpose |
|---|---|---|
| `arm skill list [--text "..."] [--source github\|local]` | `QUERY /api/skills` | Search |
| `arm skill get <name>` | `GET /api/skills/:name` | Metadata + content |
| `arm skill install <source> [--name override]` | `POST /api/skills` | Install/upsert (`github:user/repo[/path]` or a local directory) |
| `arm skill remove <name>` | `DELETE /api/skills/:name` | Remove |
| `arm tool list [--text "..."]` | `QUERY /api/tools` | Search |
| `arm tool get <name>` | `GET /api/tools/:name` | Get |
| `arm tool add <name> --command "..." [--args "..."] [--env K=V]` | `POST /api/tools` | Add an external MCP server |
| `arm tool update <name> [--command …] [--args …] [--env …]` | `PATCH /api/tools/:name` | Update |
| `arm tool remove <name>` | `DELETE /api/tools/:name` | Remove |

A skill is a directory with a `SKILL.md` (YAML frontmatter: name, description, triggers). When a
session enables `arm-skills`, the agent sees the list of its skills and loads full content on
demand with `skill_load`. Built-in tools (`arm-*`) are always available; custom tools are external
MCP servers a session can enable.

---

## Providers

| CLI | API | Purpose |
|---|---|---|
| `arm provider list` | `QUERY /api/providers` | List |
| `arm provider get <name>` | `GET /api/providers/:name` | Config + status |
| `arm provider health [<name>]` | `GET /api/providers/health`, `GET /api/providers/:name/health` | Health |

Provider CLIs change quickly; re-evaluate each one when building its connector.

---

## Completions

One-off LLM calls, no session.

| CLI | API | Purpose |
|---|---|---|
| `arm completion create -p "prompt" [--provider X] [--model X] [--system "..."] [--schema <file\|inline>] [--timeout-seconds N] [--caller X] [--retries N]` | `POST /api/completions` | One-off LLM call, no session |
| `arm completion list [--caller X] [--provider X] [--model X] [--after DATE] [--limit N]` | `QUERY /api/completions` | History |
| `arm completion get <id>` | `GET /api/completions/:id` | Get |

`provider` is optional (resolved from the model). `schema` is optional: without it the response
is plain `text` (no `data`). With a `schema`, the response also has `data` (the parsed JSON); ARM
validates it and retries up to `retries` times (default 2), and if every attempt fails it returns
422 with the raw text and the validation errors.

```json
// request
{ "prompt": "Classify this ticket as bug/feature/question", "model": "claude-sonnet-4-6[1m]",
  "systemPrompt": "You are a ticket classifier.",
  "schema": { "type": "object", "properties": { "category": {"type": "string", "enum": ["bug", "feature", "question"]} }, "required": ["category"] },
  "timeoutSeconds": 60, "retries": 2, "caller": "oribot" }
// response
{ "id": "cmp-a1b2c3", "text": "...", "data": {"category": "bug"}, "provider": "claude",
  "model": "claude-sonnet-4-6[1m]", "tokens": {"input": 150, "output": 42}, "durationMs": 1200 }
```

---

## Events (SSE)

`GET /api/events` (`arm events`) streams events. Every event has an `id`; reconnect with
`Last-Event-ID` to replay what you missed, across the retention window and server restarts. Every
event's JSON `data` has a `type` field equal to the event name. A `heartbeat` is sent every 30s.

| Event | Payload |
|---|---|
| `heartbeat` | timestamp |
| `session.created` | full session |
| `session.queued` | id, position |
| `session.started` | id, providerSessionId |
| `session.status` | id, status, previous |
| `session.completed` | id, exitCode, summary |
| `session.failed` | id, error |
| `session.killed` | id, reason, source (user\|system\|timeout) |
| `session.revived` | id, newSessionId |
| `session.text` | id, role (thinking\|assistant\|result\|user), text (complete segment, not a delta) |
| `session.tool` | id, name, toolId, args |
| `session.tool_result` | id, toolId, result, error? |
| `session.context` | id, tokens, percentage |
| `session.context_threshold` | id, percentage, threshold |
| `session.subcontext` | id, subcontextId, tokens |
| `session.message` | id, text (delivery of a user message; the conversation also gets `session.text` role=user) |
| `session.approval` | id, approvalId, kind, command, purpose, deadline |
| `session.approval_resolved` | id, approvalId, decision, actor |
| `session.approval_expired` | id, approvalId |
| `completion.start` | id, model, provider |
| `completion.done` | id, tokens, durationMs |
| `completion.failed` | id, error |
| `provider.unavailable` | provider, error |
| `server.restart_scheduled` | deadline |
| `server.draining` | remainingSessions |

**Filters** (server-side; each has an `exclude_` variant): `session`, `event` (comma list),
`caller`, `tag` (`key:value`), `role` (for `session.text`), plus `children=true|false`.

---

## Server, queue & restart

| CLI | API | Purpose |
|---|---|---|
| `arm server start [--port N] [--foreground]` | — | Start |
| `arm server stop` | — | Stop; running sessions keep going |
| `arm server status` | `GET /api/health` | Health |
| `arm server logs [-f] [--since T]` | — | Server output |

- **Queue:** FIFO with a concurrent-session limit. When all slots are busy, create returns 202
  with the queue position; when the queue is full, 503. New sessions also wait while host memory
  is low.
- **Gentle restart:** the server can exit and come back without killing running agents; the new
  server re-attaches to them and clients replay missed events via `Last-Event-ID`.
- **Retention:** sessions, logs, conversations, completions and events are kept for at least
  6 months (recent ones hot, older ones compressed but retrievable by ID), with access gated by
  auth. `secretEnv` values are never stored.

## Configuration

| Setting | Default | Description |
|---|---|---|
| `port` | 18791 | Listen port |
| `queryMethod` | `query` | Search method (`query`, `get`, `post`) |
| `totalSlots` | 10 | Max concurrent sessions |
| `maxQueueSize` | 200 | Max queued sessions |
| `backgroundTimeout` | 600 | Background session timeout (s) |
| `interactiveMaxLifetime` | 21600 | Interactive session max lifetime (s) |
| `memoryLimitMB` | 0 | Free-memory floor for starting sessions (0 = auto, 2 GB) |
| `contextThresholds` | [...] | Context-usage alert thresholds |
| `providers` | [...] | Registered providers |

---

## Integration patterns

- **Slack:** subscribe to `session.approval`, `session.text` and lifecycle events; render approvals
  as buttons; post decisions.
- **Web UI:** dashboard over SSE with an approval queue across sessions.
- **CI/scripting:** `arm session create --headless --tools arm-approved-bash --policy permissive`.
- **Custom:** any HTTP client can call the API and consume SSE.

Tools use MCP, reserved for general-purpose operations (shell, files, messaging); domain-specific
integrations belong in skills.
