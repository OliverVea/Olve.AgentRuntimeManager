# ARM — Milestones

## P0 — Spec-first toolchain ([details](P0-SPEC-FIRST.md))

- [ ] 0.1 — One POST, one GET
- [ ] 0.2 — SSE
- [ ] 0.3 — `QUERY` + `queryMethod`
- [ ] 0.4 — Errors & multi-status responses
- [ ] 0.5 — CLI
- [ ] 0.6 — Versioning
- [ ] 1.0 — Done; template write-up

## P1 — Full ARM API ([SPEC](SPEC.md))

- [ ] M1 — **Sessions core**: create/get/list/kill/delete, state machine, queue + slots, FakeProvider
- [ ] M2 — **Gentle restart**: persist session state; server exits, agents survive, new server adopts by PID (event replay completes in M3)
- [ ] M3 — **Event bus**: all SPEC event types, filters, `Last-Event-ID` replay, NDJSON persistence (incl. replay across restart)
- [ ] M4 — **Auth**: fine-grained permissions (`sessions:create`, `sessions:kill`, `approvals:decide`, …) composed into roles; default roles read-only / operator (functional: start, kill, approve; no configuration) / admin; agent tokens = session-scoped permission set; local HMAC (A3)
- [ ] M5 — **Approvals**: ARM MCP server, policies (versioned), approve/deny, long-poll wait; shell classifier as a **separate low-dependency package** (wrap an existing parser, e.g. tree-sitter-bash / mvdan/sh / bashlex, else from scratch)
- [ ] M6 — **Claude provider** (re-evaluate the current CLI first): real Claude CLI, health, record/replay LLM stub
- [ ] M7 — **Skills & tools** registries + session injection
- [ ] M8 — **Messaging + revive**
- [ ] M9 — **Completions** (schema validation, retries)
- [ ] M10 — **Codex provider** (re-evaluate the current CLI first)
- [ ] M11 — **Retention**: 6-month retention, hot/cold tiers, compressed archive
