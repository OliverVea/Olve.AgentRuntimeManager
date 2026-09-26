# ARM — Vision

Where ARM goes after V0 ([`SPEC.md`](SPEC.md)). Concrete ideas, not commitments: each gets
re-evaluated and scoped when it's picked up.

## Where agents run

- **Remote SSH executor**: run agents in a folder on a registered machine ARM holds a key for
  (host registry, `--target`/`--cwd` per session). ARM as a dispatcher across your machines.
- **OS sandbox for local agents**: cgroups + bubblewrap/nsjail for filesystem and resource
  isolation without a container runtime.
- **Per-session containers**: reproducible toolchains per session, adoption by container id.
- **Kubernetes Jobs**: agents as Jobs/Pods for scheduling, quotas and isolation (distributed mode).
- **Multi-tenant isolation**: uid-level separation between tenants' agents.
- **HA ARM**: multiple stateless replicas over shared state (Postgres `NOTIFY` for approvals and
  event fan-out); a broker only if the `session.text` firehose needs it.

## Sessions & scheduling

- **Priority**: numeric priority for sessions and completions, with reserved capacity for
  high-priority work.
- **Provider usage limits**: show how close each provider is to its usage limits, next to
  provider health. For `claude` on a subscription, Claude Code's stream-json already reports a
  `rate_limit_event` per run (`status`, `rateLimitType` such as `five_hour`, `isUsingOverage`),
  a status rather than an amount left, so the first cut is the last known status per provider
  (allowed / near the limit / limited, and the reset time when one is given). A real "X% left"
  needs a source that reports it. Later the scheduler could use it: hold queued sessions
  while a provider is limited instead of starting them to fail.
- **Effort auto-adjustment** mid-session.
- **Named agent configs**: persisted configs referenced by name at session creation instead of
  inline configuration.
- **Streaming text**: `session.text` deltas for token-by-token rendering.
- **Sleeping sessions**: a session that pauses and wakes up later, as the same session, instead
  of dying. The motivating case: sessions waiting for an approval when you clock out sit for
  hours, hit their timeout and die before you're back. Instead, a session that has waited long
  enough (for an approval, a message, …) goes to sleep: it releases its slot and its process
  (status `sleeping`, showing what it waits for), keeps its pending approval, and is woken when
  you answer it or wake it by hand, resuming its conversation. Agents (a `sleep` tool) and users
  can also put a session to sleep until a time ("check the deploy in 20 minutes") or an event
  (CI finished, a subagent ended). Timeouts then count active time, not time asleep. The UI lists
  sleeping sessions with their wake condition; they can be woken or killed.
- **Subagents**: sessions an agent starts itself. They are child sessions (M5c "Parent
  sessions": same parent link, nesting, and ending with their parent), but not only that: an
  agent may start subagents only from pre-defined arguments (e.g. named agent configs or
  templates it's allowed to use), not arbitrary prompts, providers or models; likely with
  `start_subagent`/`wait_for_subagent` tools. To be designed on top of M5c.
- **Inter-session communication and collaboration**: running sessions that know about each other
  and work together, not only parent and child. Examples: agents on related tasks sending each
  other messages, asking a peer a question and waiting for the answer, handing work over, or
  sharing a board of claims and findings so two agents don't do the same work. **Open: where it
  lives.** It may be a separate layer, service or tool rather than part of ARM. For example, a
  collaboration service the agents reach through an MCP tool, where ARM provides only the
  building blocks: session identity and agent tokens (who is speaking), delivering a message into
  a running session (M11 messaging, steering), waking a sleeping session when something
  arrives, and events other services can follow. Decide once messaging (M11) and subagents
  exist; until then keep ARM's pieces general enough that such a layer can be built on them.

## Classifications

- **`/api/classifications`**: a third entity next to sessions and completions, Jev-style. Content
  (text or JSON) plus predefined questions in (choice from a list, rubric score, yes/no); typed
  JSON answers with calibrated confidence out.
- **Backed by any provider**: providers declare capabilities (e.g. `probabilities`). A provider
  with real probabilities (model API with token probabilities, or a local classifier) answers
  directly; otherwise ARM falls back to vote sampling (N samples → vote shares).

## Server lifecycle

- **Scheduled restart with drain**: announce, stop accepting, let sessions finish, restart.
- **Hard restart**.
- **Composable dynamic configuration**: change config without restart.

## Approvals & policies

- **Typed approval payloads**: per-kind `details` instead of untyped JSON. bash → `command`,
  `workingDir`; file → `operation` (read/edit/create/delete), `path`, and a diff for edits; tool →
  `tool`, `args`. Lets frontends render rich approvals, such as diffs and syntax-highlighted
  commands.
- **Approval tokens (two-step escalation)**: on `needs approval` the tool returns a single-use,
  command-bound token and the agent must re-submit with it to actually ask a human. Gives the
  agent a chance to pick a safer command first. Parked: it didn't work well in practice (work
  ARS), so it needs a better design before it returns.
- **`suggest_safe`**: the agent proposes an auto-approval pattern alongside an approval request.
- **Policy export/import** as JSON for sharing.

## Skills & tools

- **`arm skill update [--all]`**: re-pull skills from their recorded source.
- **Skill registry protocol**: discover and install from public registries.
- **`arm-handoff` skill**: hand in-flight work to a fresh ARM session instead of writing a
  self-igniting letter to disk. The agent writes the same briefing (goal, current state, what to
  verify first, gotchas, what not to do) and the skill starts a new session with it as the
  prompt, linked to the one handing off (like `revive`, but with a fresh context). Nothing to
  delete after reading, no risk of committing it, and the handoff shows up in the session log
  and UI. Usable from inside ARM sessions and from a local agent via the `arm` CLI.

## Persistence

- **Log scrubbing**: strip sensitive content from archived logs.
- **Provider log cleanup**: strip provider-specific noise before archival.

## Frontends & interaction

- **CLI served by the server, like `pl`**: the pipeline builds `arm` (linux-x64/arm64,
  darwin-arm64) into the bundle, the image carries it, and ARM serves it at `/download/{asset}`
  (outside `/api`, anonymous). Each environment serves the CLI matching its API; `arm` can warn
  when it's older than the server, or update itself.
- **Named servers in the CLI** (same idea for `pl`): `~/.arm/config.json` lists servers by name
  (`"servers": { "prod": "https://arm-private.ovea.pro", "beta": "https://arm-beta.ovea.pro" }`,
  plus a `default`), picked with `--server beta` or `ARM_SERVER=beta`; `--url` still wins.
  Credentials stay per URL, and `arm login` no longer changes the default server as a side effect
  (today it does, so the next command quietly goes to the last server logged in to).
- **Setup page without auth**: the web UI's logged-out view (and `/setup`) shows the install
  one-liner for the viewer's OS from this server's `/download`, then `arm login`, plus the server
  version. Anonymous because it's what you need before you have a token; nothing on it is secret
  (the OIDC client is already public via `/api/auth-config`). Fine while ARM is Tailscale-only;
  revisit with a public route, like `/health` (OPEN-QUESTIONS C1).
- **Mobile-first web UI**: on a phone the jobs are checking status, approving and sending a
  quick steering message; the laptop view is the larger surface built on top of that.
- **Dashboard widget**: a small embeddable ARM widget (running/queued/waiting counts, pending
  approvals with inline approve/deny) for a homelab dashboard next to Olve.Pipelines and other
  services. Served by ARM at `/widget` (the dashboard only hosts it, e.g. as an iframe) and
  configured by query parameters: `view` (`summary`/`approvals`/`sessions`), filters reusing the
  session search fields (`caller`, `tag`, `status`, `provider`), and `compact`/`theme`/`limit`.
  Parameters are configuration, never authorisation: no tokens in the URL; the widget uses the
  normal login and they can only narrow what the viewer may already see. Unknown or invalid
  values fall back to defaults. Olve.Pipelines needs the same `/widget` convention (e.g. pipeline
  status, pending promotion gates): file an issue there.
- **`arm chat`**: interactive conversational session in the terminal.
- **`arm run "prompt"`**: stream output to stdout, exit with the session's exit code.
- **Interactive CLI approvals**: approval prompts inline, keyboard approve/deny.
- **WebSocket channel** for interactive frontends, with a binary encoding (protobuf from the same
  TypeSpec models) behind `--binary`.
- **Python / Bash clients** generated from the same contract.

## API

- **`QUERY` (RFC 10008)** for searches, next to or instead of `POST /api/<resource>/search`
  (optionally a `queryMethod` switch: `query` | `post`). Blocked until the toolchain carries it:
  TypeSpec has no `QUERY` verb and Hey API drops OpenAPI 3.2 `query` operations (M4 spike,
  2026-09-26); .NET 11 routes it natively.

## Standards

- **OpenAI-compatible endpoint** (`/v1/chat/completions`) so any OpenAI SDK can use ARM.
- **ACP endpoint** (Agent Client Protocol): ARM as an ACP agent, so any ACP client (Zed,
  JetBrains, the `arm` CLI) can attach to an ARM session as a frontend, with ARM's policy,
  approvals and queue behind it. Also the hedge if ACP grows to cover most of ARM's API: ARM
  shrinks toward this endpoint instead of being scrapped (OPEN-QUESTIONS A10).
- **AG-UI** (agent-to-user protocol): the closest fit for a frontend-agnostic runtime.
- **A2A** (agent-to-agent) for multi-agent orchestration.
- **OpenTelemetry** traces per session.
