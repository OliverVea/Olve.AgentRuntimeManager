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
- **Subagents**: agents starting sessions of their own in a controlled way, which is more than a
  parent link (M5c "Parent sessions"). An agent may only start subagents from pre-defined
  arguments (e.g. named agent configs or templates it's allowed to use), not arbitrary prompts,
  providers or models; likely with `start_subagent`/`wait_for_subagent` tools. To be designed.

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

## Persistence

- **Log scrubbing**: strip sensitive content from archived logs.
- **Provider log cleanup**: strip provider-specific noise before archival.

## Frontends & interaction

- **CLI served by the server, like `pl`**: the pipeline builds `arm` (linux-x64/arm64,
  darwin-arm64) into the bundle, the image carries it, and ARM serves it at `/download/{asset}`
  (outside `/api`, anonymous). Each environment serves the CLI matching its API; `arm` can warn
  when it's older than the server, or update itself.
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
