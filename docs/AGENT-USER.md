# Agents as their own user — design note

2026-09-27. Builds on M5d (agents with all of Claude Code's tools, the VM as the sandbox) and
M5a (per-session supervisors, [GENTLE-RESTART.md](GENTLE-RESTART.md)).

## The threat

Until now ARM's agents ran as the VM user `arm`: the user of the ARM service, and the deploy SSH
user, with passwordless sudo. An agent runs with `--permission-mode bypassPermissions` and does
what its prompt, the repos it clones and the web pages it reads lead it to, so anything `arm` can
do, an agent (or whoever steers one by prompt injection) can do too:

- `sudo` anything: root on the VM;
- read `/etc/olve-arm/env` (the config, the Claude token, on prod the OTLP client secret), ARM's
  database (every session's prompt, output and env), and `arm`'s home (where the deploy host's
  SSH key is authorised: an agent could add its own);
- signal ARM itself, or rewrite its releases in `/opt/olve-arm`.

## The choice

**Agents run as `arm-agent`**, a plain user: no sudo, no password, its own home. ARM starts each
session's supervisor as that user, so the supervisor and the agent under it are `arm-agent`
processes and everything they write is `arm-agent`'s.

**ARM keeps running as `arm`.** Its own user (without sudo) would limit what a compromise of ARM
itself (its HTTP API, behind Authentik) gets, but that's a separate threat, and moving the
service needs its own migration (the state's ownership, `arm`'s Claude login check, supervisors
started by the old service); it doesn't change what an agent can reach. Worth doing next; the
agent user is built so it doesn't need redoing then (ARM only needs the sudoers rule below and
membership of the agent user's group, not root).

**How ARM starts a process as the agent user: `sudo -n -u arm-agent -- <supervisor>`**, allowed
by a sudoers rule for exactly the supervisor binary (`/etc/sudoers.d/olve-arm-agent`):

```
arm ALL=(arm-agent) NOPASSWD: /opt/olve-arm/releases/*/olve-arm-supervisor, /opt/olve-arm/current/olve-arm-supervisor
```

`arm` could already sudo anything; the rule is what's left for ARM when `arm` loses that. Sudo
fits the supervisor as it is: the launch still goes over stdin (sudo passes it through), the
supervisor still detaches itself (`setsid`), sudo resets the environment (the supervisor gets
the agent's as data anyway), and the target is a user with less than ARM, so the rule can't be
turned into more. Considered and not taken:

- *setuid/capabilities* (`CAP_SETUID` for ARM or on the supervisor binary): that's the power to
  become any user, root included, where sudo's rule names one target user;
- *`systemd-run --uid`* per session (a transient unit): needs root or a polkit rule, doesn't take
  the launch on stdin, and moves the agent out of the service's cgroup, which the gentle restart
  relies on (`KillMode=process`);
- *the supervisor as `arm`, only the agent as `arm-agent`*: the supervisor then can't kill its
  agent's process tree (another user's), and would need the same sudo for that.

`sudo -n` never waits for a password: a missing rule fails the launch (at once, or at the
supervisor's connect timeout), and sudo's refusal is in the journal.

## What changes in ARM

Configuration `Supervisor:User` (`Supervisor__User=arm-agent` in `env.beta`/`env.prod`). Unset,
as in local dev and the tests: supervisors run as ARM's own user, as before. Set:

- **Launch** (`Supervisors.Launch`): `sudo -n -u <user> -- <supervisor>` (`Supervisor:Sudo`,
  default `sudo`); the agent's `HOME`, `USER` and `LOGNAME` are the agent user's (looked up with
  `getent passwd`; a missing user fails the launch, it never falls back to ARM's user). So Claude
  Code keeps its state, mise its toolchains and ssh its keys in `/home/arm-agent`.
- **Session folders:** ARM makes each session's folder and its `work/` group-writable and setgid
  (`2770`); the group, `arm-agent`, comes from `sessions/` being setgid `arm-agent`. The
  supervisor's files (`output.jsonl`, `stderr.log`, `supervisor.json`, `exit.json`) are then
  `arm-agent`'s, mode `0640`, and ARM (a member of the group) reads them. ARM refuses to share a
  folder that is a link.
- **Sockets:** under `/run/olve-arm/supervisors`, which the unit makes `2770 arm:arm-agent`
  (`ExecStartPre=+install …`, on every start: `/run` is cleared on reboot). The supervisor makes
  its socket `0660`, and **accepts connections only from ARM's uid** (`ClientUid` in the launch,
  checked with `SO_PEERCRED`): otherwise any agent, being the same user as every supervisor,
  could attach to another session's supervisor and write to that agent's stdin. The protocol
  change is one additive launch field; a supervisor from before accepts anyone (its socket was
  `0600` then).
- **Killing:** ARM kills a live run through its supervisor's socket, as before. An orphan (the
  supervisor gone, its agent not) belongs to another user, so ARM runs
  `sudo -n -u arm-agent -- <supervisor> kill <process group> <pid>` (the start-time check that
  the pid is still the agent stays in ARM, reading `/proc`). ARM also still signals it directly,
  which kills a run from before the agent user.

## The VM (`vm-deploy.sh` → `agent-user.sh`, every deploy, idempotent)

- the user `arm-agent` (home `0700`), `arm` in its group, the sudoers rule (checked with
  `visudo -c` before it's installed);
- `/var/lib/olve-arm` `0711` (passable, not listable), `arm.db*` `0600` (the unit's
  `UMask=0027` keeps new files from others; SQLite gives its WAL and SHM the database's mode);
- `sessions/` `2750 arm:arm-agent`: only ARM makes session folders, so an agent can't swap one;
- once, on a VM from before (while `sessions/` isn't `arm-agent`'s): every existing session
  folder becomes group `arm-agent`, group-writable, setgid (the owner stays `arm`, so an agent
  still running as `arm` across the deploy keeps writing its own), and `~arm/.claude/projects`
  is copied to the agent user (continuing an earlier session resumes its Claude session, which
  Claude Code keeps there);
- the deploy key `~arm/.ssh/arm-agent-deploy` moves to `~arm-agent/.ssh/`. The registered
  `GIT_SSH_COMMAND` (`ssh -i ~/.ssh/arm-agent-deploy …`) needs no change: `~` is now the agent's.
  A new key goes to `/home/arm-agent/.ssh/arm-agent-deploy` (`0600`, owned by `arm-agent`).

Agents start with an empty home: toolchains installed with mise as `arm` aren't carried over;
an agent installs them again, as on a new VM.

## What an agent can and can't reach

Can:
- its session's folder and workplace, its home (mise, Claude Code's state, the deploy key),
  `/tmp`, the network (GitHub over SSH, the internet, ARM's API on `localhost:5000`, which
  wants a token);
- the Claude token and the session's env (in its environment, by design);
- the releases and Claude Code in `/opt/olve-arm` (read and run, not write);
- **other sessions: their folders, their agents and supervisors.** All agents are the one user,
  so an agent can read and write another session's workplace and output, kill its agent or
  supervisor, or replace its socket. What it can't is attach to another supervisor's socket
  (the uid check). Separating sessions from each other needs a user per session (and then a
  shared toolchain elsewhere than one home): not now.

Can't:
- `sudo`; `/etc/olve-arm/env`; ARM's database; `arm`'s home (and so the host's authorised key
  and ARM's own Claude login check); write `/opt/olve-arm`;
- signal ARM, or anything else that isn't `arm-agent`'s;
- list `/var/lib/olve-arm` or create session folders.

## Verified, and not

- Unit tests (`AgentUserTests`), with the test's own user standing in for the agent user and a
  stub for sudo: a session launched through sudo (the call, the agent's identity), the session
  folders shared, an orphan killed through `sudo … kill`, the `kill` command on a process group,
  and the socket refusing another uid. The sudo rule and the user switch itself can't run there.
- After the first deploy with this, on beta: the deploy's Claude check (a Haiku session through
  ARM) runs as `arm-agent`; then by hand: `ps -o user` of a supervisor and its agent;
  `sudo -l` fails for the agent; it can't read `/etc/olve-arm/env` or `arm.db`; a session that
  pushes over SSH with the moved key; a restart mid-session re-attaches (gentle restart).
