# ARM — Milestones

Dependency-ordered. Toolchain details for M1–M4 and M15: [`SPEC-FIRST.md`](SPEC-FIRST.md).
The `arm` CLI grows with every milestone.

- [x] M1 — **Spec toolchain**: one POST + one GET on `Message`; mise, root `package.json`, Hey API client, route-coverage + contract tests, minimal `arm` binary
- [x] M2 — **Backend generator**: our own TypeSpec emitter generating the backend surface (records, typed `IHandler` interfaces, route mapping, validators, result → status mapping) into `artifacts/`; migrate `Message` onto it
- [x] M3 — **SSE**: typed events with `type`, server-side filters, `Last-Event-ID` replay, `arm events`
- [x] M4 — **Sessions core**: create/get/search/kill/delete, state machine, queue + slots, timeouts, FakeProvider; search as `POST …/search` (`QUERY` moved to VISION); error envelope + typed multi-status responses; `Idempotency-Key`; **`Message` example removed entirely** — the API contains only ARM entities. In memory until M5
- [ ] M4b — **Providers & models**: `GET /api/providers`, each provider with its models; ids plus display names (optional in the server's configuration, falling back to the id; always present in the API) and each model's context window. Clients: `arm provider list`, the CLI's `--model` checked against it, and the web UI's model dropdown ("Opus 5.5 (Claude Code)")
- [x] M5 — **Persistence**: sessions in SQLite through EF Core (A5), migrations at startup; after a restart, working sessions are killed (source `system`) and queued ones queue again
- [ ] M5a — **Gentle restart**: server exits, agents survive, new server re-attaches (event replay completes in M6)
- [ ] M5b — **Session transcript** (the log view, a core feature): every turn of a session (user prompt, agent text and thinking, tool calls with arguments and results, messages) stored per session and read with one call (`GET /api/sessions/{id}/conversation`), plus counts (turns, messages, tool calls); the fake provider scripts turns so it's built LLM-free. Clients: `arm session conversation <id>` and the web UI's session detail ([`UI-REFERENCE.md`](UI-REFERENCE.md)). Following a running session live can come with M6: clients subscribe to the events and fetch the snapshot in parallel, buffer events until the snapshot arrives, then apply the ones it doesn't already contain (dedupe by a per-session sequence number that entries and events both carry, e.g. `seq` vs the snapshot's `lastSeq`). The web UI's session list gets the same treatment (today it can lose or overwrite an event while its first search loads)
- [ ] M5c — **Parent sessions**: `CreateSession` accepts an optional `parentId` and the session carries it; search can filter by parent. When a parent ends (completed, failed, cancelled or killed), its children end too: queued ones are cancelled, working ones killed, source `system`, reason naming the parent. ARM gives every agent its own session id in the environment (`ARM_SESSION_ID`), so an agent's `arm session create` picks it up as the parent automatically; `--parent <id>` overrides it and `--no-parent` opts out. Web UI: children nested under their parent (rail, fold, "+N ended"), as in the mock; the UI has no way to set a parent. (Agent-started subagents are a different thing, see VISION "Subagents")
- [ ] M6 — **Event bus**: all SPEC event types, filters, replay incl. across restart
- [ ] M7 — **Auth**: fine-grained permissions composed into roles; default roles read-only / operator (start, kill, approve; no configuration) / admin; session-scoped agent tokens; local tokens (A3)
- [ ] M8 — **Approvals**: approval tools, policies (versioned), approve/deny, waiting calls (design them so a long wait can later become a sleeping session rather than a timeout, see VISION "Sleeping sessions"); shell classifier as a **separate low-dependency package** (wrap an existing parser, e.g. tree-sitter-bash / mvdan/sh / bashlex, else from scratch)
- [ ] M9 — **Claude provider** (re-evaluate the current CLI first): real Claude CLI, health, record/replay LLM stub
- [ ] M10 — **Skills & tools** registries + session injection
- [ ] M11 — **Messaging + revive**
- [ ] M12 — **Completions** (optional schema validation, retries)
- [ ] M13 — **Codex provider** (re-evaluate the current CLI first)
- [ ] M14 — **Retention**: 6-month retention, hot/cold tiers, compressed archive
- [ ] M15 — **API versioning** (B3)

## Web UI

The approved target for the sessions screen is the mock
[`src/frontend/mocks/sessions.html`](../src/frontend/mocks/sessions.html) (open it in a browser;
its banner switches between the target design and what today's API supports). Each milestone
builds its part of it in the web UI, alongside its API and CLI (STANDARDS, Clients); UI work
stays mock-first, so parts the mock doesn't show get a mock of their own first. The mock is
deleted once the whole screen is built and approved.

| Mock feature | Milestone |
|---|---|
| Overview (queued/working, oldest first) and History (ended, newest first); composer (prompt, model, caller, timeout; Ctrl+Enter; "Advanced" on phones); kill ✕ with reason, delete (trash); card: status · duration, task, id · model · caller; copy id; click a time for clock times; Options (defaults for new sessions, theme); light/dark; Tab/Shift+Tab/Delete keys; clickable cards → the session page | Done 2026-09-26 (the web UI catch-up), on today's API plus no timeout by default, `cancelled` for queued sessions and search by several statuses; the model is a `provider/model` text field until M4b |
| Model dropdown with display names | M4b |
| Live updates: the Overview (and later the session page) follows the event stream — subscribe and fetch the snapshot in parallel, buffer, apply what the snapshot lacks (per-session `seq`) — and the live dot shows the stream's state | Done 2026-09-26 for the Overview (on today's `session.*` events, re-reading the snapshot once subscribed); the `seq` dedupe with M5b/M6 |
| BETA marker with the build version (beta only) | Done 2026-09-26 (`GET /api/server-info`) |
| Log column: last 3 lines, filling from the bottom; expand; wrap (the box keeps its height); fade at the edges; links; outcome as the last line; wrap/expand defaults in Options | M5b |
| Session page (details + the full transcript) | M5b — **needs its own mock first** |
| Context use (% ↔ tokens/window, green < 15%, yellow < 30%, red from 30%) | M6 (context events), from real numbers with M9 |
| Child sessions nested under their parent (rail, fold, "+N ended"); no parent field in the composer | M5c |
| Sleeping sessions | Not planned yet (VISION) |
