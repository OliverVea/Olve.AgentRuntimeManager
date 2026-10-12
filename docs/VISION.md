# ARM — Vision

Where ARM goes after V0 ([`SPEC.md`](SPEC.md)). Concrete ideas, not commitments: each gets
re-evaluated and scoped when it's picked up.

## Where agents run

- **Remote SSH executor**: run agents in a folder on a registered machine ARM holds a key for
  (host registry, `--target`/`--cwd` per session). ARM as a dispatcher across your machines.
- **OS sandbox for local agents**: cgroups + bubblewrap/nsjail for filesystem and resource
  isolation without a container runtime.
- **A network and Docker of its own per session** (spiked on beta 2026-10-12; scripts in the
  gitignored `sandbox/isolation-spike/`): so several agents can work on the same service at once
  (the aim: 5 on one repo, 10–20 in all) without fighting over ports, compose projects or
  containers. Each session's agent starts in its own network namespace with its own rootless
  Docker daemon (rootlesskit + slirp4netns, no ports forwarded to the VM), and the agent, the
  daemon and every container sit in one systemd slice of the agent user (which needs lingering).
  Proven with two sessions at once: the same published ports, compose projects of the same name,
  Testcontainers, an ARM image build each, and an ARM restart under both. From inside, ARM is
  reached at the VM's address or its public URL, not `127.0.0.1`.
  - **ARM's own code ends a session's slice, never an agent** (a hard requirement): the
    supervisor stops the slice when the agent exits or is killed, and ARM stops every session
    slice without a live session at startup. Today's kill doesn't reach into the slice: in the
    spike a killed session's agent, daemon and containers all kept running.
  - ARM also removes the session's Docker data (owned by sub-uids, so through rootlesskit, not a
    plain delete). It's about 4 GB after one ARM image build, since daemons share nothing: the
    data goes on its own virtual disk (NVMe first; the host's HDD if space runs out, later
    perhaps an NVMe cache in front of it with LVM), and a shared registry mirror saves the pulls.
  - The VM needs sizing for it (about 200 MiB per idle session before the agent; builds take the
    CPU). One VM holds every session; more machines come with the executors above.
  - Devcontainers fit on top as something an agent uses per repo (`devcontainer up` in its own
    Docker), not as what a session runs in: a session has zero or several repos.
- **A user per session**: sessions above still share one agent user, so an agent can reach
  another session's network, Docker socket, slice and files (the gap in
  [`AGENT-USER.md`](AGENT-USER.md), a little wider). A user per session or slot, each with its own
  sub-uid range, closes it; ARM already starts each supervisor as a named user.
- **Reaching a session's services**: open what a session's agent is running (its web UI, its
  API) from your browser or CLI, through ARM: a proxy into the session's network, since nothing
  is published on the VM. The spike only reached in from the VM itself.
- **Kubernetes Jobs**: agents as Jobs/Pods for scheduling, quotas and isolation (distributed mode).
- **Multi-tenant isolation**: uid-level separation between tenants' agents.
- **HA ARM**: multiple stateless replicas over shared state (Postgres `NOTIFY` for approvals and
  event fan-out); a broker only if the `session.text` firehose needs it.

## Sessions & scheduling

- **Priority**: numeric priority for sessions and completions, with reserved capacity for
  high-priority work.
- **Provider usage limits**: show how close each provider is to its usage limits, before it hits
  them (M9 already pauses a provider once it has: `limited` until the reset). For `claude` on a
  subscription, Claude Code's stream-json reports a `rate_limit_event` per run (`status`,
  `rateLimitType` such as `five_hour`, `isUsingOverage`, `resetsAt`): a status rather than an
  amount left, so a first cut is the last status per provider. A real "X% left" needs a source
  that reports it. The scheduler could then hold low-priority sessions near a limit.
- **Message queue** (2026-10-12; Oliver: "show me the queue at the bottom … that'd also allow me
  to cancel messages"): today a message to a working session goes to the agent at once and shows
  in the conversation only when the agent reads it, after its current step, so on the session
  page "sent, not yet read" looks like "never sent", and nothing can be taken back. Instead ARM
  keeps every message itself (in its database, in order, so they survive a restart) and hands
  the waiting ones to the agent when its current step ends (a tool call returning, a turn
  ending: ARM sees both in the agent's output). A message is `waiting` (cancellable) and then
  handed over (in the conversation as a prompt, as today). A queued session's messages follow
  its prompt and an ended session is continued, both as today. Sending returns the message's
  id; a read lists a session's waiting messages and a delete cancels one (refused once handed
  over). Web UI: the queue at the bottom of the session page, above the message box, each
  message with its text, when it was sent and a cancel button (mock first); CLI: list and
  cancel. Open: what a kill does to waiting messages (drop them, keep them undelivered, or let
  the first one continue the session).
- **Session profile** (2026-10-12): where a session's time went, as its own read next to the
  conversation and activity (`arm session profile <id>`, a breakdown on the session page): time
  in the model (thinking and writing, taken from the gaps between steps), in each tool, in builds
  and tests, and in each subagent (broken down inside it too, since its entries are kept), the
  slowest single steps, and a timeline of the session's phases. All from what ARM already stores
  (every conversation entry is timed and tool calls pair with their results). Done by hand once
  for an 84-minute session: 44% in four review subagents, 33% in the model, 19% in builds and
  tests. With a little more: tokens and cost per session and per step (the provider's raw output
  carries usage, which ARM keeps but doesn't expose), and totals across sessions (which kinds of
  task are slow, how many review rounds pay off). It can't say why a command was slow.
- **Effort auto-adjustment** mid-session.
- **Named agent configs**: persisted configs referenced by name at session creation instead of
  inline configuration.
- **Secret environment variables** (moved here 2026-09-27; plain `env` comes first): `secretEnv`
  per session and secrets registered in ARM, write-only (never returned, logged or put in events),
  kept in a k8s Secret per environment the way Olve.Pipelines keeps pipeline secrets (the VM
  reaching k3s with a ServiceAccount token scoped to that one Secret).
- **Streaming text**: `session.text` deltas for token-by-token rendering.
- **Revive** (moved here from M11 on 2026-09-27: a message already continues an ended session in
  place): a new, independent session seeded with an ended one's conversation, with its own tools,
  skills and policy (`arm session revive <id> -p "follow-up"`, `session.revived`).
- **Sleeping sessions**: a session that pauses and wakes up later, as the same session, instead
  of dying. The motivating case: sessions waiting for an approval when you clock out sit for
  hours, hit their timeout and die before you're back. Instead, a session that has waited long
  enough (for an approval, a message, …) goes to sleep: it releases its slot and its process
  (status `sleeping`, showing what it waits for), keeps its pending approval, and is woken when
  you answer it or wake it by hand, resuming its conversation. Agents (a `sleep` tool) and users
  can also put a session to sleep until a time ("check the deploy in 20 minutes") or an event
  (CI finished, a subagent ended). Timeouts then count active time, not time asleep. The UI lists
  sleeping sessions with their wake condition; they can be woken or killed. Freezing a run by
  hand is the same thing (2026-10-12): no snapshot of the machine, only what a session already
  keeps, its workplace (the git worktrees) and the agent's history, from which it continues
  later. Its containers are gone by then; the agent starts what it needs again.
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
- **VM size from the pipeline** (2026-10-12; while ARM deploys into VMs): each VM's CPUs, memory
  and disk are numbers in `vm-deploy.sh` today, used only when the VM is first made. Keep them as
  defaults, let the pipeline's deploy steps override them with variables, and apply a changed size
  to an existing VM too (CPUs and memory at its next restart, since restarting ends its sessions).

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
- **Protected paths** (2026-10-12): files an agent shouldn't change without a say-so, given per
  session and per repo: each of a session's `repos` carries its own patterns, e.g.
  `["docs/VISION.md", ".git/**"]` (defaults per repo can come with a registry of repos). Two
  places can hold it, and
  neither is the agent itself (it has a shell, so a rule on its edit tools only stops accidents):
  a policy on ARM's own file tools, denying the edit or asking for approval when it is tried
  (M8, M10); and a check in ARM's code when a session ends, listing what its branch changed
  that matches (with the check of finished branches after M5e). Until then it's the brief and
  the review.

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

- **File previews** (added 2026-09-27, with M17's files): files in a conversation, sent or
  handed back, open in the web UI without downloading: CSV and Excel as tables, Word and PDF as
  documents, text and Markdown rendered, PNG/JPEG and other images inline, and so on for other
  common types.
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
- **OpenTelemetry** traces per session (2026-10-12: the other shape of the session profile above):
  each turn, tool call and subagent as a span, with the model's time and token counts where
  known, sent to the collector ARM already reports to. Timelines and flame graphs for every
  session, and comparisons across sessions, in the telemetry stack that's already there, without
  ARM drawing any of it. The two aren't exclusive: the profile read is the quick one, traces the
  more powerful.
