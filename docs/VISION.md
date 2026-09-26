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
- **AG-UI** (agent-to-user protocol): the closest fit for a frontend-agnostic runtime.
- **A2A** (agent-to-agent) for multi-agent orchestration.
- **OpenTelemetry** traces per session.
