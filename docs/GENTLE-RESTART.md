# Gentle restart (M5a) — design note

Agreed 2026-09-27. Builds on OPEN-QUESTIONS A10 (leaning of 2026-09-26). Goal: a deploy (or any
ARM restart) no longer kills working agents; the new server re-attaches to them, and where it
can't, it finishes or resumes the session instead of killing it.

Today `Recover()` kills every `working` session ("ARM restarted; the agent was lost."), because
the agent is ARM's child: its stdin/stdout are pipes into the ARM process, and when ARM dies the
agent finishes its turn unsupervised and exits (spike). The VM service already has
`KillMode=process`, so systemd leaves the children alone; what's missing is someone to hold the
pipes.

## Shape

```
ARM ──unix socket──▶ olve-arm-supervisor (one per session) ──stdio──▶ claude -p …
                          │
                          ├─ <WorkRoot>/<id>/output.jsonl   (agent stdout, raw)
                          ├─ <WorkRoot>/<id>/stderr.log
                          ├─ <WorkRoot>/<id>/supervisor.json (run id, log offset, pids, version)
                          └─ <WorkRoot>/<id>/exit.json       (run id, exit code/signal)
```

- **Every launch is a run.** A session's folder holds several runs: retries already append to
  the same `output.jsonl`, and a resume will too. So each launch (attempt or resume) gets a run
  id (`providerSessionId` + a run counter, stored on the session record), and `supervisor.json`
  records it together with the `output.jsonl` offset where that run's output starts.
  `exit.json` carries the run id; ARM ignores one that isn't the session's current run.
  Rebuilding and `attach` start at the run's offset, never at 0 (else a refused attempt's
  `api_error` result would be read as the live run's). The session record gains fields (run
  counter), the API doesn't.

- **The supervisor is dumb and provider-agnostic.** It starts the command it's given, holds the
  agent's stdio, writes stdout to `output.jsonl` and stderr to `stderr.log` (raw, unframed: M5b
  builds the transcript from exactly these lines), writes `exit.json` when the agent ends, and
  serves a socket. It knows nothing about Claude or stream-json.
- **ARM keeps all provider logic.** `ClaudeRun` still sends the prompt, reads the turn, decides
  the outcome (`Outcome()`), closes input after the `result` and applies the exit grace — but on
  a supervised process instead of a `Process`. Concretely, `ClaudeRun` moves onto a small
  `IAgentProcess` (write a line, close input, read lines from an offset, stderr tail, exit,
  kill), with the supervisor client as its implementation.
- **One source of truth for the launch.** ARM builds the full argv and environment (today's
  `ClaudeProvider.StartInfo` + `LockDownEnvironment`) and hands them to the supervisor in its
  launch request. The supervisor never composes flags, so the lockdown stays in one place (plus
  its twin in `.pipelines/scripts/claude-code.sh`, unchanged). The supervisor itself is started
  with an empty environment (it gets the agent's env as data), so it holds none of ARM's secrets
  (OTel client secret, etc.).
- **One writer per file.** ARM stops writing `output.jsonl`/`stderr.log`; it only reads them.

## Who ends the turn while ARM is away

`ClaudeRun` ends the agent by closing stdin after the first `result`. With ARM gone, the
supervisor could close stdin on disconnect (the agent then finishes and exits, as in the spike),
but that rules out approvals (M8: `can_use_tool` answered on stdin) and steering (M11) across a
restart.

**Decision: the supervisor keeps stdin open while ARM is away.** An agent that finishes its turn
meanwhile just idles. The reconnecting ARM replays `output.jsonl` from the current run's
offset, sees the `result`, closes input and applies the exit grace as usual.
(Since M11 a run may have several turns, one per message: `ClaudeRun` closes stdin after a
`result` once every message it gave the agent has been taken up, and takes the last `result`. A
re-attached run doesn't know what the previous server sent, so it closes input after the first
`result`; nothing already written is lost, as the agent works through its input before it exits,
and each new turn's replayed message holds off the exit grace.)
An agent waiting on an approval simply waits for the new ARM. The price: an idle agent holds its
slot until ARM is back, which is seconds on a deploy.

## Protocol

Newline-delimited JSON over a Unix socket, one client at a time (a new connection replaces the
old one: that's the old ARM being gone).

- **Hello, both ways, with a protocol version.** The protocol is frozen and additive: new fields
  and message types may be added, nothing changes meaning, unknown fields are ignored. ARM N+k
  must talk to a supervisor from release N (a long session can outlive several deploys), so
  there is no "current and previous version" window; a real break needs a new major, and ARM
  keeps speaking every major that can still be running.
- **Launch is not on the socket.** `IAgentProvider.Start` runs under `_gate` and must not block,
  so ARM doesn't spawn, wait for a socket, then send a launch. It spawns the supervisor with the
  launch spec (argv, env, cwd, run id, paths, and the agent's first input: its prompt) written
  to the supervisor's stdin and closed: a small pipe write. The supervisor starts the agent and
  writes the prompt at once (no window with a supervisor but no agent, or an agent without its
  prompt, if ARM goes away right after the launch), and `ClaudeRun`'s background task connects. `Start` keeps a cheap synchronous check
  that the command exists (`MissingCommand_FailsToStart` stays true); anything else that goes
  wrong in the launch arrives as the run's outcome, after `session.started`.
- **The supervisor detaches itself:** `setsid()` at startup (P/Invoke; `Process.Start` can't),
  stdio on `/dev/null` (its diagnostics go to `supervisor.log`), and every descriptor inherited
  from ARM without close-on-exec closed (.NET opens its own close-on-exec; a leaked pipe would
  stay open as long as the agent runs). So ARM exiting breaks nothing, and Ctrl+C on a local
  `dotnet run` doesn't reach it through the process group.
- **Detaching on ARM's side:** `Supervisors.Dispose` (the server stopping) closes every
  connection without stopping the agents; the tests use it to model ARM dying.
- **ARM → supervisor:** `attach {offset}`, `write {line}`, `closeInput`, `kill` (the agent's
  process tree, at once, as before the supervisor; a grace can be added to the message later).
- **Supervisor → ARM:** `hello {version, runId, supervisorPid, agentPid, outputStart,
  stderrStart}`, `output {line, end}` (lines after the attach offset, `end` the byte offset after
  the line; the file is written first, so it's always ahead), `exited {exitCode}` (128 + the
  signal when a signal ended it).
- After the agent exits the supervisor writes `exit.json` (temp + rename), sends `exited` if
  someone's attached, and exits. Nothing lingers: the files are the record.

## Recovery (`Recover()`), per working session

In this order, with the I/O done outside `_gate`:

1. **The current run's supervisor answers** → re-attach: a `ClaudeRun` rebuilt from the log (result seen? signals)
   continues on the live socket.
2. **No supervisor, the current run's `exit.json` exists** → the agent ended while ARM was away: finish with the
   real outcome, through the same `Outcome()` (log + exit code + stderr tail).
3. **No supervisor, no exit file, but the current run's log ends with a successful `result`** (no message taken up after it) → the work is done:
   complete (exit code 0, as for an agent stopped for lingering). Don't resume finished work.
4. **Otherwise** → resume (below). Two agents on one Claude session must not happen.

Before 3 and 4: make sure the old agent isn't an orphan still running (supervisor crashed,
agent didn't). `supervisor.json` has the agent's pid, process group and start time (from
`/proc/<pid>/stat`); a matching live group is killed.
5. **Provider can't resume** (the fake) → killed, source `system`, as today.

A socket file that refuses the connection means the supervisor is gone (no stale-file cleverness).

Bookkeeping in `SessionManager`: re-attached and resumed runs go into `_running` with
`ProviderState.Started` **before** `StartNextLocked`, or the queue overfills the slots. The
timeout is re-armed with what's left (`StartedAt + TimeoutSeconds − now`); an expired one kills
at once (source `timeout`). `CheckProvidersAsync` runs after recovery, as now; re-attached
sessions don't wait for it. Provider health stays in memory and starts `available` (unchanged;
`ProviderHealthTests` keep their rules).

`IAgentProvider` gets one new member, roughly
`IAgentRun? Recover(AgentRecovery recovery)` (session id, prompt, model, attempt,
providerSessionId), returning a run for cases 1–4 or null for 5. The default returns null.

## Resume

- `claude -p … --resume <providerSessionId>` in the same working directory, through a new
  supervisor, with the same lockdown. Resume reuses the stored id (unlike a retry, which needs a
  new `--session-id`).
- `-p` needs a user message to continue; proposed text: *"ARM restarted while you were working.
  Continue where you left off; if you were in the middle of a tool call, check its effect before
  repeating it."*
- It's not a new attempt: `Attempts`/`retriesLeft` don't change. If the resume itself is refused
  (`Unavailable`), the normal retry/pause rules apply to it.
- Requirement this surfaces (spike: a killed agent's tool call can arrive twice): ARM's own tools
  must be idempotent per tool-call id. Nothing to do in M5a (no ARM tools yet); noted for M8/M10.

## Where things live

- **No secrets on disk.** `supervisor.json` never stores argv or env: the env carries
  `CLAUDE_CODE_OAUTH_TOKEN` (and later `secretEnv`).
- **Sockets in a runtime dir.** Not a security boundary: agents ran as ARM's uid, so any path
  ARM can reach, a tool-using agent (M8/M10) could too; that's the sandboxing question, not M5a.
  (Since [AGENT-USER.md](AGENT-USER.md) agents and their supervisors run as their own user, and a
  supervisor only takes connections from ARM's uid.)
  The reasons are that `/run` is cleared on reboot and paths stay short. Sockets go under
  `Supervisor:SocketRoot`: on the VMs
  `/run/olve-arm/supervisors` (systemd `RuntimeDirectory=olve-arm` with
  `RuntimeDirectoryPreserve=yes`, else a restart deletes them; `/run` is cleared on reboot, which
  is right: no supervisor survives one either). Name: `<session id>.sock`, well inside the
  108-byte limit; the local-dev default is `$XDG_RUNTIME_DIR/olve-arm/` or `/tmp/olve-arm-<uid>/`.
- **Surviving release pruning.** `vm-deploy.sh` keeps two releases and deletes the rest,
  including Claude Code versions no kept release links to; a supervisor (and its agent) from
  release N can run while N gets deleted. A running single-file binary survives deletion on
  Linux; a multi-file .NET app that lazy-loads an assembly afterwards does not. So the
  supervisor is **Native AOT** (one small file, fast start, no runtime to load), published as
  `olve-arm-supervisor` next to ARM's executable. The `claude` binary is already a single file
  (verify: that a running `claude` never re-executes itself by path, which would break once its
  version folder is pruned). Needs an AOT-capable SDK image in the Dockerfile's build stage
  (`sdk:10.0-noble-aot`, verified 2026-09-27). (Fallback if AOT is a
  hassle: single-file self-contained, trimmed, or copying the binary into the runtime dir at
  launch.)
- **Configurable path** (`Supervisor:Command`, default: next to ARM's own executable), so tests
  and local dev find it.
- **Where it doesn't help:** a container restart or VM reboot kills everything; there only
  cases 2–4 apply (in practice: resume).

## Project and tests

- New project `Olve.AgentRuntimeManager.Supervisor` (console, no ASP.NET, AOT), plus the shared
  protocol types. Built by `dotnet build`, published by the Dockerfile, shipped by
  `vm-deploy.sh` as part of `/app` (no script change beyond the unit's `RuntimeDirectory`).
- Unit tests for the supervisor (launch, relay, exit file, kill, reconnect, replay from offset).
- Restart tests in UnitTests, next to `ClaudeProviderTests` and its stub CLI (TESTING L2), with
  the real supervisor: start a session, drop the first `SessionManager` (its supervisor
  connections closed, agents left running: it must model ARM dying, not an old ARM still
  reading), recover with a new one on the same store, WorkRoot and SocketRoot, and assert each
  recovery case: re-attach and complete; ended while away (exit file); a retry's leftover
  `exit.json` ignored; supervisor killed → orphan killed, resumed (the stub accepts
  `--resume`); finished-but-unrecorded → completed; timeout expired while away → killed. No
  real Claude calls.
- What in-process tests can't show: `KillMode=process`, `RuntimeDirectoryPreserve`, the AOT
  binary outliving its pruned release, `setsid` under systemd. One manual check on beta when
  it lands: a stub-CLI session (or the one rationed Haiku run) spanning two deploys. Not in the
  pipeline.

## Contract

**No `src/spec/` change.** `Session` stays `working` across a restart, and nothing new is exposed.
Open question for Oliver: should a resume be visible (a `session.resumed` event, or a count on
`Session`)? If yes, that's a separate spec proposal.

## Compared with A10 and aoe's runner

- As A10: supervisor per session, separate internal executable, owns stdio, log + exit code on
  disk, socket, resume as the fallback, versioned protocol, rebuild from the log.
- Beyond A10: stdin stays open while ARM is away (for M8/M11); the provider logic stays in ARM and
  the supervisor only relays bytes; runs identified within a session's folder; a finished-but-unrecorded turn completes instead of resuming;
  orphan check before resume; frozen-additive protocol instead of version windows; AOT so it
  survives release pruning; sockets outside the agent's reach.
- vs aoe's `__acp-runner`: it speaks the provider's own protocol, not ACP. State is rebuilt from
  the log on disk, not from the runner's memory. The runner exits as soon as its agent does.

## Status (2026-09-27)

Built (`Olve.AgentRuntimeManager.Supervisor`, `Sessions/Supervision/`, `ClaudeProvider.Recover`,
`SessionManager.Recover`), with the restart tests above (`ClaudeRecoveryTests`,
`SessionManagerTests.Restart_*`). Checked by hand: the AOT binary from the image keeps serving
after its file is deleted, and a second client replays the run from its start. On beta
(2026-09-27, release 20260927-090643): the deploy's Haiku check ran under a supervisor; a Haiku
session with `systemctl restart olve-arm` 4 s into its turn kept its supervisor (`KillMode=process`,
sockets in the preserved `/run/olve-arm`), was re-attached by the new server ("Recovered 1
working"), and completed (attempt 1, exit 0). Not yet seen for real: the resume path (supervisor
lost), and whether a running `claude` re-executes itself by path (the release-pruning case).

Known limits:
- Sessions working while the first release with supervisors deploys have no run id (their agent
  was the old server's child) and are killed, as before.
- A supervisor that dies while its server is up fails the session (`SupervisorLostException`);
  only a restart resumes.
- `Supervisors.Launch` writes the launch (with the prompt) to the new supervisor's stdin under the
  runtime's lock: it waits only for the supervisor's startup read, even for a large prompt.

## Decided with Oliver (2026-09-27)

1. A10's gentle-restart leaning holds as the base.
2. Stdin stays open while ARM is away.
3. A resume is not an attempt; it continues with the message above.
4. Resume stays invisible in the API for now (no spec change).
5. The supervisor is Native AOT.
