# ARM V2 — Requirements Spec

*Renamed from the original ARS V2 draft (`ARS`→`ARM`, `ars`→`arm`); content otherwise unchanged. ARM = Agent Runtime Manager.*

**Scope:** This document specifies the V0 (minimum shippable) surface. Features explicitly marked "Future" are out of scope for V0 but inform the architecture.

## What ARM Is

A standalone, general-purpose agent runtime. It manages LLM agent sessions (spawn, supervise, queue), provides a real-time event bus (SSE), brokers tool approvals, and manages skills/tools configuration. Provider-agnostic (Claude, Codex, extensible to others). Frontend-agnostic (CLI, web, Slack, custom integrations are all equal SSE consumers).

No opinion on what agents do — ARM handles the plumbing (process lifecycle, approval flows, skill/tool distribution, streaming, persistence) and stays out of the way.

### HTTP Method Conventions

- `GET` — retrieve a single resource by ID (e.g. `GET /api/sessions/:id`)
- `QUERY` — search/filter/list resources (request body carries filter criteria) — per RFC 10008
- `POST` — create a resource or trigger an action
- `PATCH` — partial update
- `DELETE` — remove a resource

`QUERY` replaces the traditional `GET` + query-string pattern for collection endpoints. It's safe and idempotent (like GET) but accepts a request body, making complex filters natural without URL encoding gymnastics.

**Implementation:** minimal API routes `QUERY` via `MapMethods` on .NET 10; .NET 11 supports it natively. The API contract (`src/spec/main.tsp`) defines each search operation once.

**Query method is configurable** (`queryMethod: "query" | "get" | "post"`, default `"query"`). If deployed behind infrastructure that doesn't support QUERY, switch to `"get"` (query-string encoding) or `"post"` (POST to `/api/<resource>/search`).

### Error Model

All error responses use a consistent envelope:

```json
{
  "error": {
    "code": "APPROVAL_EXPIRED",
    "message": "Approval arm-apv-xyz expired after 30 minutes",
    "details": {}
  }
}
```

**HTTP status semantics:**

| Status | Meaning |
|---|---|
| 400 | Malformed request body / invalid parameters |
| 401 | Missing or invalid auth token |
| 403 | Authenticated but not authorized (e.g. messaging disabled) |
| 404 | Resource not found |
| 409 | Conflict (e.g. approve an already-resolved approval, kill an already-dead session) |
| 410 | Resource existed but was purged (retention GC) |
| 422 | Semantically invalid (e.g. schema validation failure on completions) |
| 429 | Rate limited |
| 503 | Server draining (restart scheduled) |

**Race conditions:**
- Two frontends approving the same approval: first wins (200), second gets 409
- Message to a queued session: 409 (session not yet running)
- Revive on a running session: 409

---

## Authentication

`arm login` authenticates the user and issues a signed token.

**Zero-config mode:** If no auth is configured, ARM generates a signed token on first login (HMAC-SHA256 with a server-generated secret key stored in the data directory). The token is handed to the user (printed to stdout / stored in a credential file). Suitable for single-user local dev.

**Multi-user / remote mode:** ARM validates tokens against its configured auth backend. Tokens carry identity and permissions.

**Permissions:** fine-grained (`sessions:create`, `sessions:kill`, `approvals:decide`, …). The auth system composes them into roles. Default roles: read-only, operator (functional: start, kill, approve; no configuration) and admin.

**Agent auth:** Sessions get a scoped token via env (`AGENT_RUNTIME_TOKEN`): a permission set limited to that session. The token grants only session-specific operations (approvals, messages, skill loads for that session). An agent cannot self-approve — approval decisions require `approvals:decide`.

**Actor verification:** The `actor` field in approval decisions is derived from the auth token, not user-supplied. This ensures the audit trail is tied to identity.

**Isolation boundary:** Agent tokens should not be readable by the agent's subprocesses. The ARM MCP server holds the token internally; spawned commands (via `approved_bash`) receive a scrubbed environment. Full uid-level isolation is a future consideration for multi-tenant deployments.

---

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│                       ARM Server                            │
│                                                            │
│  Session Manager │ Event Bus │ Approval Engine │ Scheduler │
│  Provider Registry │ Skill Registry │ Tool Registry        │
│  ARM MCP Server                                            │
└────────────────────────────────────────────────────────────┘
        │                │
   ┌────┴────┐     ┌────┴────┐
   │ Agent   │     │ Agent   │
   │ (claude)│     │ (codex) │
   └─────────┘     └─────────┘
```

The ARM MCP server is a core component that always runs. Individual tool modules within it are optionally connected per-session based on the session's `tools` configuration.

Frontends (CLI, web UI, custom) subscribe to SSE and call REST. They are not part of ARM.

---

## Providers

### Interface

A provider implements: `spawn(session) → childProcess`, `parseStream(line) → event`, `parseStderr(line) → event`, `onExit(code, signal) → event`, `buildResumeArgs(session) → {bin, args}`, `kill(session)`, `buildEnv(session) → env`, `buildCommand(session) → {bin, args}`, `buildCwd(session) → path`, `checkHealth() → HealthStatus`. ARM normalizes all provider output into its unified event model.

**Approval mechanism:** Providers are spawned with their native permissions disabled (no stdin for interactive approval). All tool calls route through the ARM MCP server, which blocks the tool response until the approval engine resolves it. This is the sole approval path — provider-native approval doesn't work because there's no interactive terminal.

### Claude

- CLI: `claude -p "..." --model '...' --output-format stream-json --verbose --dangerously-skip-permissions --mcp-config <path>`
- Supports: `--effort`, `--resume`
- Stream format: NDJSON (assistant/user/system/tool types)
- Resume: native via `--resume <providerSessionId>`
- Approval: ARM MCP server tools are the only bash/file tools available. Claude never sees native Bash/Write/Edit.

### Codex

- CLI: `codex exec "..." --json -m <model> --dangerously-bypass-approvals-and-sandbox`
- Effort: `-c model_reasoning_effort=<level>`
- MCP: TOML config in isolated `CODEX_HOME` per session
- Stream format: thread/turn/item events → normalized to ARM model
- Resume: `codex exec resume <threadId> "prompt"`
- Note: `--ephemeral` only used for completions (non-resumable).

### Adding a Provider

Drop a module implementing the provider interface. Register in config. No core changes needed.

---

## Entities & Operations

### Sessions

| CLI | API | Purpose |
|---|---|---|
| `arm session create -p "prompt" [--provider X] [--model X] [--effort X] [--caller X] [--tag k:v] [--timeout-seconds N] [--headless] [--messaging\|--no-messaging] [--env K=V] [--secret-env K=V] [--policy X] [--tools T1,T2] [--skills S1,S2] [--idempotency-key K]` | `POST /api/sessions` | Create (queues or starts) |
| `arm session list [--status X] [--caller X] [--tag k:v] [--after DATE] [--before DATE] [--limit N] [--offset N]` | `QUERY /api/sessions` | Search/filter sessions |
| `arm session get <id>` | `GET /api/sessions/:id` | Get by ID (includes computed status) |
| `arm session kill <id> [--reason "..."]` | `POST /api/sessions/:id/kill` | Kill running/queued/waiting |
| `arm session delete <id>` | `DELETE /api/sessions/:id` | Delete record (terminal states only) |
| `arm session revive <id> [-p "follow-up prompt"] [--tools T1,T2] [--skills S1,S2] [--idempotency-key K]` | `POST /api/sessions/:id/revive` | Create new session continuing from this one |
| `arm session logs <id> [-f] [--since TIME] [--until TIME]` | `GET /api/sessions/:id/logs` | View log (SSE if -f) |
| `arm session conversation <id> [--format json\|text]` | `GET /api/sessions/:id/conversation` | Structured conversation |
| `arm session message <id> "text"` | `POST /api/sessions/:id/messages` | Send a message to the session |

**Idempotency:** All mutating operations (`create`, `revive`) support an optional `Idempotency-Key` header (API) or `--idempotency-key` flag (CLI). Duplicate requests with the same key return the original response. Keys are retained for 24 hours.

**Session search body (QUERY):**
```json
{
  "status": "working",
  "caller": "oribot",
  "tags": {"team": "seller-growth"},
  "createdAfter": "2026-08-01T00:00:00Z",
  "createdBefore": "2026-08-10T00:00:00Z",
  "text": "deploy",
  "limit": 20,
  "offset": 0
}
```
All fields optional. Empty body returns all sessions (same as bare `arm session list`).

**Status** (state machine, single value):
- `queued` → `working` → `completed`
- `queued` → `killed`
- `queued` → `failed` (validation failure at dequeue)
- `working` → `waiting` (pending approval) → `working` (approval resolved or denied — agent decides what's next)
- `working` → `killed`
- `working` → `failed`
- `waiting` → `killed`

Terminal states: `completed`, `killed`, `failed`. These do not transition further. Revival creates a new linked session (see below).

**Future:**
- Effort auto-adjustment mid-session.
- Priority (numeric, applies to sessions and completions).

**Session creation payload:**
```json
{
  "prompt": "...",
  "provider": "claude",
  "model": "claude-sonnet-4-6[1m]",
  "effort": "high",
  "messaging": true,
  "headless": false,
  "caller": "oribot",
  "tags": {},
  "env": {},
  "secretEnv": {},
  "timeoutSeconds": 600,
  "approvalPolicy": "standard",
  "tools": ["arm-approved-bash", "arm-file-ops", "arm-skills", "arm-messaging"],
  "skills": ["brazil", "git-workflows"],
  "systemPrompt": "Optional additional system prompt.",
  "idempotencyKey": "optional-unique-key"
}
```

Agent configuration is passed **inline at session creation**. The `tools` array lists which ARM MCP tool modules to enable. The `skills` array lists which skills are available (only injected if `arm-skills` is in the tools list).

**`secretEnv`:** Write-only environment variables for sensitive values (API keys, tokens). Never returned in GET responses, scrubbed from logs and event persistence. Only the ARM MCP server's `approved_bash` uses them (injected into the subprocess env at execution time).

**Future:** Persisted named agent configs that can be referenced by name at session creation.

**Messaging:** Sessions can receive user messages via `POST /api/sessions/:id/messages`. Enabled by default; can be disabled at session creation (`messaging: false`). When disabled, the endpoint returns 403.

**Headless mode:** When `headless: true`, the ARM approval layer auto-denies anything that doesn't pass automatic approval (the policy's `allow` rules). The underlying provider still gets `--dangerously-skip-permissions` / approve-all because ARM is the gate, not the provider. Useful for CI/scripting.

**Revival:** `POST /api/sessions/:id/revive` creates a **new** session with the parent's conversation history injected as context. The parent session record is unchanged (stays in its terminal state) and gains a `childSessionId` field. The new session is fully independent — it gets its own tools, skills, and policy (specified in the revive call or defaulting to the parent's). Provider-native `--resume` is NOT used; instead the parent's conversation is prepended. This avoids fork conflicts (reviving A to B, then A to C — both are independent fresh sessions with A's history).

### Approvals

| CLI | API | Purpose |
|---|---|---|
| — | `POST /api/sessions/:id/approvals` | Register pending (called by ARM MCP server internally) |
| `arm approval list [--session X] [--status pending\|approved\|denied\|expired] [--kind bash\|file\|tool] [--after DATE] [--limit N]` | `QUERY /api/approvals` | Search approvals |
| `arm approval get <session-id> <approval-id>` | `GET /api/sessions/:id/approvals/:aid` | Get by ID |
| `arm approval approve <session-id> <approval-id> [--reason "..."]` | `POST /api/sessions/:id/approvals/:aid/decide` | Approve |
| `arm approval deny <session-id> <approval-id> [--reason "..."]` | `POST /api/sessions/:id/approvals/:aid/decide` | Deny |
| — | `GET /api/sessions/:id/approvals/:aid/wait` | Long-poll until resolved (used internally by MCP server) |

**Decision payload:**
```json
{
  "decision": "approve | deny",
  "reason": "optional"
}
```

`actor` is derived from the auth token — not supplied in the payload.

**Approval token protocol:** When a command is classified as `needs_approval`, the MCP tool returns a structured error containing an `approvalToken`. The agent can re-call with the token to escalate to the user. The token is:
- Single-use (consumed on submission)
- Session-scoped
- TTL: 15 minutes (configurable)
- Cryptographically bound to the command + working_dir (HMAC; server recomputes and verifies on submission)

The actual approval decision is made by ARM/the user after seeing the full command and context surfaced via SSE. The token is for escalation flow control, not authorization.

### Skills

| CLI | API | Purpose |
|---|---|---|
| `arm skill list [--text "..."] [--source github\|local]` | `QUERY /api/skills` | Search/filter installed skills |
| `arm skill get <name>` | `GET /api/skills/:name` | Get skill metadata + content |
| `arm skill install <source> [--name override]` | `POST /api/skills` | Install (upsert) from source |
| `arm skill remove <name>` | `DELETE /api/skills/:name` | Remove |

**Future:** `arm skill update [--all]` — re-pull from recorded source.

**Sources:**
```bash
arm skill install github:user/repo              # git repo
arm skill install github:user/repo/skills/name  # subdirectory
arm skill install ./local-path/                  # local directory
```

A skill is a directory containing at minimum a `SKILL.md` file with YAML frontmatter (name, description, triggers). Installed to `~/.agent-runtime/skills/<name>/`.

**Session injection:** At session start, the available skill list (names + descriptions) is injected into the agent's system prompt **only if `arm-skills` is in the session's tools list**. The agent calls `skill_load` to retrieve full content on demand.

### Tools (MCP Server Modules)

| CLI | API | Purpose |
|---|---|---|
| `arm tool list [--text "..."]` | `QUERY /api/tools` | Search/list configured tools |
| `arm tool get <name>` | `GET /api/tools/:name` | Get tool config |
| `arm tool add <name> --command "..." [--args "..."] [--env K=V]` | `POST /api/tools` | Add a tool definition |
| `arm tool remove <name>` | `DELETE /api/tools/:name` | Remove |
| `arm tool update <name> [--command "..."] [--args "..."] [--env K=V]` | `PATCH /api/tools/:name` | Update config |

Tools are modules exposed by the ARM MCP server. Built-in modules (`arm-*`) are always available. Custom tool definitions (external MCP servers) are compiled into the provider-specific config at spawn time.

**No tools are enabled by default.** The session's `tools` array explicitly lists which modules to activate. Without any tools, the agent has no system access — it can only do LLM reasoning.

### Providers

| CLI | API | Purpose |
|---|---|---|
| `arm provider list` | `QUERY /api/providers` | List registered providers |
| `arm provider get <name>` | `GET /api/providers/:name` | Get provider config + status |
| `arm provider health [<name>]` | `GET /api/providers/health` or `GET /api/providers/:name/health` | Overall or per-provider health |

### Completions (one-off LLM calls, no session)

| CLI | API | Purpose |
|---|---|---|
| `arm completion create -p "prompt" [--provider X] [--model X] [--system "..."] [--schema <file\|inline>] [--timeout-seconds N] [--caller X] [--retries N] [--idempotency-key K]` | `POST /api/completions` | Single LLM call |
| `arm completion list [--caller X] [--provider X] [--model X] [--after DATE] [--limit N]` | `QUERY /api/completions` | Search completion history |
| `arm completion get <id>` | `GET /api/completions/:id` | Get by ID |

**Completion creation payload:**
```json
{
  "prompt": "Classify this ticket as bug/feature/question",
  "provider": "claude",
  "model": "claude-sonnet-4-6[1m]",
  "systemPrompt": "You are a ticket classifier.",
  "schema": {
    "type": "object",
    "properties": {
      "category": {"type": "string", "enum": ["bug", "feature", "question"]},
      "confidence": {"type": "number"}
    },
    "required": ["category", "confidence"]
  },
  "timeoutSeconds": 60,
  "retries": 2,
  "caller": "oribot"
}
```

`provider` is optional — if omitted, ARM auto-resolves from model name.

**Response:**
```json
{
  "id": "cmp-a1b2c3",
  "text": "...",
  "data": {"category": "bug", "confidence": 0.95},
  "provider": "claude",
  "model": "claude-sonnet-4-6[1m]",
  "tokens": {"input": 150, "output": 42},
  "durationMs": 1200
}
```

When `schema` is provided, ARM instructs the model to return JSON conforming to the schema and validates the output. If validation fails, ARM retries up to `retries` times (default 2). If all attempts fail, returns 422 with the raw text and validation errors.

### Server

| CLI | API | Purpose |
|---|---|---|
| `arm server start [--port N] [--foreground]` | — | Start in background (or foreground) |
| `arm server stop` | — | Soft stop (sessions survive, server exits) |
| `arm server status` | `GET /api/health` | Health + diagnostics |
| `arm server logs [-f] [--since TIME]` | — | View server output |

**Future:** Scheduled restart with drain, hard restart.

### Events

| CLI | API | Purpose |
|---|---|---|
| `arm events [--session X] [--exclude-session X] [--event X,Y] [--exclude-event X,Y] [--caller X] [--exclude-caller X] [--tag k:v] [--exclude-tag k:v] [--role X] [--exclude-role X] [--exclude-children] [--last-event-id ID]` | `GET /api/events` | Subscribe to SSE stream |

---

## Interactive Modes (Future)

The APIs support interactive use from V0, but dedicated CLI modes are a later addition.

### `arm chat` (Future)

Interactive conversational session via CLI.

### `arm run "prompt" [...]` (Future)

Non-interactive task mode: streams output to stdout, exits with session exit code.

---

## Event Bus (SSE)

### Endpoint

`GET /api/events` — Server-Sent Events stream. Every event has an `id:` field for reconnection via `Last-Event-ID`.

### Events

| Event | Payload |
|---|---|
| `heartbeat` | timestamp |
| `session.created` | Full session metadata |
| `session.queued` | id, position |
| `session.started` | id, providerSessionId |
| `session.status` | id, status, previous |
| `session.completed` | id, exitCode, summary |
| `session.failed` | id, error |
| `session.killed` | id, reason, source (user\|system\|timeout) |
| `session.revived` | id, newSessionId |
| `session.text` | id, role (thinking/assistant/result/user), text (complete, not delta) |
| `session.tool` | id, name, toolId, args |
| `session.tool_result` | id, toolId, result, error? |
| `session.context` | id, tokens, percentage |
| `session.context_threshold` | id, percentage, threshold |
| `session.subcontext` | id, subcontextId, tokens |
| `session.message` | id, text (user→agent message delivered) |
| `session.approval` | id, approvalId, kind, command, purpose, deadline |
| `session.approval_resolved` | id, approvalId, decision, actor |
| `session.approval_expired` | id, approvalId |
| `completion.start` | id, model, provider |
| `completion.done` | id, tokens, durationMs |
| `completion.failed` | id, error |
| `provider.unavailable` | provider, error |
| `server.restart_scheduled` | deadline |
| `server.draining` | remainingSessions |

**`session.text`:** Each event carries a complete text segment (not a delta/chunk). Frontends render them sequentially. Future: streaming/delta mode.

**`session.message` vs `session.text role=user`:** Both may fire for user messages. `session.message` is for message delivery tracking; `session.text` is for rendering the conversation.

**Heartbeat:** Server sends `heartbeat` event every 30s.

**Payload discriminator:** every event's JSON `data` carries a `type` field equal to its event name, so generated clients get per-event types.

### Event Persistence

Events are written to daily-rotated NDJSON files on disk. This serves as both audit log and replay source. Each event includes timestamp and monotonic event ID.

**In-memory buffer:** Last 1 day of events kept in memory for fast replay on reconnect. Older events served from disk.

### Server-Side Filtering

All filter params support both include and exclude variants:

| Param | Exclude variant | Purpose |
|---|---|---|
| `session=<id>` | `exclude_session=<id>` | Filter by session |
| `event=t1,t2` | `exclude_event=t1,t2` | Filter by event type |
| `caller=X` | `exclude_caller=X` | Filter by session caller |
| `tag=key:value` | `exclude_tag=key:value` | Filter by session tag |
| `role=thinking\|assistant\|result` | `exclude_role=X` | Filter session.text |
| `children=true\|false` | — | Include/exclude child sessions |

**Reconnection:** Include `Last-Event-ID` header to replay missed events from the buffer/disk.

---

## Approval System

### How It Works

The ARM MCP server is the sole path to system operations (shell, file, messaging). When an agent calls `approved_bash`:

1. ARM classifies the command against the session's policy
2. If `safe`: execute immediately, return stdout
3. If `blocked`: return error with reason
4. If `needs_approval`: return `approvalToken` → agent can re-submit to escalate → ARM emits `session.approval` on SSE → MCP tool call blocks (defer-and-resume for human-latency decisions) → frontend responds → tool call resolves

Provider-native approval doesn't exist in this architecture — there's no interactive terminal, and provider permissions are disabled at spawn. ARM is the only gate.

### ARM MCP Tool Modules

**Recommended tool modules** (each enabled independently via session `tools` array):

| Module | Tools | Purpose |
|---|---|---|
| `arm-approved-bash` | `approved_bash` | Execute shell commands through approval policy |
| `arm-file-ops` | `approved_file_read`, `approved_file_edit`, `approved_file_create`, `approved_file_delete` | File operations through path policy |
| `arm-skills` | `skill_load`, `skill_list` | Skill discovery and loading |
| `arm-messaging` | `message_user`, `check_user_replies` | Bidirectional user communication |

**Parameters (approved_bash):**
```json
{
  "command": "git push origin main",
  "purpose": "Deploy fix",
  "working_dir": "/path/to/repo",
  "approvalToken": "optional — include to escalate a previously-denied command"
}
```

**When blocked:**
```json
{
  "error": "BLOCKED",
  "reason": "rm commands are not allowed in this policy"
}
```

**When needs approval:**
```json
{
  "error": "NEEDS_APPROVAL",
  "reason": "npm install requires human approval",
  "approvalToken": "arm-tok-abc123"
}
```

**File reading:** `approved_file_read` is subject to path policy. Policies can allow reads broadly while restricting writes.

**Timeout:** The MCP tool call uses defer-and-resume semantics for human-latency decisions. The ARM server sets a reasonable timeout (configurable, tested to work with both Claude and Codex providers). If the timeout expires before a decision, the approval auto-denies.

**Future:** `suggest_safe` — on the approval response, the agent can include a suggested pattern for auto-approval.

### Classification

```
command → policy evaluation → safe | blocked | needs_approval
  safe → execute immediately, return stdout
  blocked → return error with reason
  needs_approval → return approvalToken → agent re-submits → SSE event → defer → decision → resume
```

The classifier must be shell-aware with a fail-closed default. Covered by extensive unit tests including adversarial inputs. It ships as a separate, low-dependency package (preferably wrapping an existing shell parser).

### Policies (Application Data)

Policies are **versioned application data**. Each mutation creates a new version. Old versions without any referencing sessions are garbage-collected.

| CLI | API | Purpose |
|---|---|---|
| `arm policy list [--text "..."]` | `QUERY /api/policies` | List all policies |
| `arm policy get <name> [--version N]` | `GET /api/policies/:name` | Get policy rules (latest or specific version) |
| `arm policy create <name> [--from <file>]` | `POST /api/policies` | Create policy |
| `arm policy update <name> [--from <file>]` | `PATCH /api/policies/:name` | Update rules (creates new version) |
| `arm policy delete <name>` | `DELETE /api/policies/:name` | Delete (fails if sessions reference it) |
| `arm policy test <name> [--file commands.txt]` | — | Test policy against commands (reads stdin if no file) |
| `arm policy history <name> [--limit N]` | — | View version history |

**Future:** `arm policy export/import` — export as JSON for sharing.

**Policy structure:**
```json
{
  "name": "standard",
  "version": 3,
  "rules": [
    {"action": "allow", "pattern": "git status", "kind": "bash"},
    {"action": "allow", "pattern": "git log *", "kind": "bash"},
    {"action": "allow", "pattern": "cat *", "kind": "bash"},
    {"action": "deny", "pattern": "rm -rf *", "kind": "bash", "reason": "Destructive operation. Use approved_file_delete for individual files."},
    {"action": "deny", "pattern": "git push --force *", "kind": "bash", "reason": "Force push is never safe."},
    {"action": "allow", "pattern": "file-read", "kind": "file", "paths": ["**"]},
    {"action": "allow", "pattern": "file-edit", "kind": "file", "paths": ["/tmp/**", "./src/**"]},
    {"action": "deny", "pattern": "file-edit", "kind": "file", "paths": ["/etc/**", "/home/*/.ssh/**"], "reason": "System files are off limits."}
  ]
}
```

Rules are evaluated in order; first match wins. Unmatched commands require approval.

---

## Persistence

Storage layout TBD before implementation. Leaning: one persistence port; locally SQLite + NDJSON files (self-contained, no daemon), in production an ARM-owned Postgres.

**Requirements:**
- Session metadata, logs, and conversations retained for at least 6 months
- Hot storage for recent sessions (active + last N days)
- Cold/archive storage for older sessions (compressed, queryable by ID)
- Event files: daily rotation, 1 day in-memory, older from disk
- Completions history retained similarly
- Access to logs gated by the same auth system
- `secretEnv` values never persisted in any log or event

**Future:**
- Log scrubbing: strip sensitive content from archived logs.
- Provider log cleanup: strip provider-specific cruft before archival.

---

## Configuration

Location/filename TBD.

| Setting | Default | Description |
|---|---|---|
| `port` | 18791 | Listen port |
| `queryMethod` | `"query"` | HTTP method for search endpoints (`query`, `get`, `post`) |
| `totalSlots` | 10 | Max concurrent sessions |
| `maxQueueSize` | 200 | Max queued sessions (reject above this) |
| `backgroundTimeout` | 600 | Background timeout (s) |
| `interactiveMaxLifetime` | 21600 | Interactive max (s) |
| `memoryLimitMB` | 0 | 0=auto (block when <2GB free) |
| `contextThresholds` | [...] | Token alerts |
| `providers` | [...] | Registered provider configs |

**Deployment:** V0 runs as a host process (systemd user unit) with agent processes detached, so agents survive a server restart. CI/CD via [Olve.Pipelines](https://github.com/OliverVea/Olve.Pipelines). Containerized/k8s deployment is Future.

**Future:** Composable dynamic configuration.

---

## Restart & Adoption

Soft restart (the only mode in V0): Server exits. Agent processes survive (detached). New server adopts by PID + log replay from the event files.

**Future:** Scheduled restart with drain, hard restart. Distributed mode with agents as Kubernetes jobs.

---

## Queue & Scheduling

- FIFO queue with configurable slot limit
- Configurable max queue size (default 200). When queue is full: `POST /api/sessions` returns 503 (rejected, try again later)
- Memory gate: blocks spawns when system RAM < 2GB free
- Configurable per-session timeout
- When slots are full but queue has room: `POST /api/sessions` returns 202 Accepted with queue position

**Future:** Numeric priority with reserved capacity for high-priority sessions.

---

## Standards & Extensibility

### Current

- **MCP** (Model Context Protocol) for tool integration — used sparingly, only for truly general-purpose operations (shell, file I/O, user communication). Domain-specific integrations should be skills with scripts.
- **SSE** for real-time streaming — standard event-stream protocol with `Last-Event-ID` replay
- **REST/JSON** for all management APIs
- **HTTP QUERY method** (RFC 10008) for search/filter operations

### Future

- **OpenAI-compatible chat completions endpoint** (`/v1/chat/completions`) — makes ARM usable as a backend for any OpenAI-SDK client
- **A2A (Agent-to-Agent)** protocol support for multi-agent orchestration
- **AG-UI (Agent-to-User)** protocol — closer fit than A2A for a frontend-agnostic runtime
- **Skill registry protocol** — standardized discovery/installation from public registries
- **OpenTelemetry** — structured observability for session traces
- **WebSocket** — bidirectional streaming for interactive frontends

---

## Integration Patterns

### Headless (Slack)

Subscribe to SSE (`session.approval`, `session.text`, lifecycle events). Render approvals as Slack buttons. Post decisions via REST.

### CLI Interactive (Future)

Local session. Approval prompts rendered inline. Keyboard approve/deny.

### Web UI

SSE for dashboard. Approval queue view across all sessions.

### CI/Scripting

`arm session create --headless --tools arm-approved-bash --policy permissive` — auto-approves everything within policy.

### Custom

Any HTTP client can consume SSE and call REST. The approval contract is frontend-agnostic.

---

## Next Steps

See [`MILESTONES.md`](MILESTONES.md). The API contract is TypeSpec (`src/spec/main.tsp`); OpenAPI
documents and clients are generated build artifacts. The backend is checked against the contract
by route-coverage and contract tests. Open: API version strategy (version prefix or header).
