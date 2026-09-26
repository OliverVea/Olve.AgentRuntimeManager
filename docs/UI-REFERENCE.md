# UI reference

**The approved design is the mock [`src/frontend/mocks/sessions.html`](../src/frontend/mocks/sessions.html)**
(sessions screen; which milestone builds which part: [`MILESTONES.md`](MILESTONES.md#web-ui)).
This page is the background it came from.

What the web UI should grow into, described from screenshots of the agent runtime ARM is
modelled on. The screenshots stay local (`docs/mocks/`, gitignored: they show internal names and
this repo is public); this is the shareable description. It's a direction, not a spec: each
screen lands in the milestone that builds its API, and the web UI may simplify (STANDARDS,
Clients). Every item below also needs its CLI counterpart.

## Session list (home)

- **Header:** product name, server version (commit + build date), navigation (History, Usage,
  Skills, Tools, Completions, Memory) and a live "N active, N queued" indicator.
- **Stat tiles:** active, queued, slots (`used/total`), memory used by sessions, system memory
  available, uptime.
- **Active sessions:** one card per running/waiting session. Left: status badge (Working /
  Waiting), agent name, the prompt, session id, model and effort, caller tag, custom tags.
  Middle: a **live tail of the transcript** (last agent text, tool calls as
  `tool → argument summary`, with a cursor while streaming). Right: context use (`47K/1M`),
  elapsed time, turn and tool-call counts.
- **Recent sessions:** the same card for ended ones (status badge, agent, effort badge, summary,
  id, model, caller; right: context use, duration, how long ago, turns) and "View all" →
  history.

### Nested flows (subagents)

- A session that starts subagents shows them as full session cards **directly below it**,
  indented, joined to it by one vertical rail on the left; the parent card has a
  "▾ N subagents" toggle that folds them away.
- The parent's log tail shows the fan-out: `start_subagent → <agent>` (its result carries the
  child's id), repeated per child, then `wait_for_subagent → [ids]` while it waits.
- A child card is a normal session card: status, agent, prompt, id, provider badge,
  model · effort, caller, tags (with a `[+N]` overflow), its own log tail and counters.
- Only running subagents are listed under an active parent; ended ones aren't shown there.
- Log tails show tool calls as `⚙ tool → args` with the result on the next line (`↳ …`,
  truncated), a live cursor on the newest line, and "▾ Expand (N lines)" to open the rest.

## Session detail (the log view: a core feature)

- Back link to all sessions; status badge and agent name.
- **Metadata rows:** id, summary, model, caller, duration, started, context use; priority,
  working directory, provider session id.
- **Transcript:** a header line (`N turns · N messages · context 73K/1M`), then the messages in
  order: the user prompt rendered as markdown, agent text, thinking, tool calls with their
  arguments and results, messages to and from the user. For an ended session it's one read of
  the stored transcript; for a running one it follows live.

## Usage

- Range buttons (1d/3d/7d/14d/30d) and filters (model, caller, agent).
- Tiles: sessions, output tokens, input (context) tokens, estimated cost, total turns.
- A chart of tokens and sessions per day, and a per-session table (session, model, agent,
  turns, output, input, cost, completed).

## Skills and Tools

- One page each: a name filter, a time range and a sort.
- Tiles: total loads/calls, unique skills/tools, average per skill/tool.
- A table per skill/tool: count (with a bar), a 14-day sparkline, sessions, last used, and the
  agents that use it.

## Completions

- Tiles: running, total served, recent.
- A table: status, caller, model, duration, prompt and output size, time, id.

## Memory

- Tiles: memory of the whole agent process tree, system memory available, active sessions.
- Per session: its process tree (command line, memory per process), collapsible, with a refresh.

## Dashboard widget

A compact card for a personal dashboard (see VISION): "Agent sessions" with an active count and
a history link. Per session: a status dot, short id, where it came from (e.g. a chat link), a
context-use bar with a percentage, status (working, waiting for your reply, queued), the prompt's
start, the latest output lines, and how long ago.

## Concepts the screens use that ARM doesn't have yet

Named **agents** (a saved configuration a session runs as; VISION "Named agent configs"),
**effort** as a badge, **tags**, a **summary** per session, **context and token usage**, **turn
and tool-call counts**, estimated **cost**, **priority** and **working directory**. Each arrives
with the milestone that implements it, following the barebones-contract rule (SPEC-FIRST).
