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

## Server lifecycle

- **Scheduled restart with drain**: announce, stop accepting, let sessions finish, restart.
- **Hard restart**.
- **Composable dynamic configuration**: change config without restart.

## Approvals & policies

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

## Standards

- **OpenAI-compatible endpoint** (`/v1/chat/completions`) so any OpenAI SDK can use ARM.
- **AG-UI** (agent-to-user protocol): the closest fit for a frontend-agnostic runtime.
- **A2A** (agent-to-agent) for multi-agent orchestration.
- **OpenTelemetry** traces per session.
