# ARM — Milestones

Dependency-ordered. Toolchain details for M1–M4 and M15: [`SPEC-FIRST.md`](SPEC-FIRST.md).
The `arm` CLI grows with every milestone.

- [x] M1 — **Spec toolchain**: one POST + one GET on `Message`; mise, root `package.json`, Hey API client, route-coverage + contract tests, minimal `arm` binary
- [ ] M2 — **Backend generator**: our own TypeSpec emitter generating the backend surface (records, typed `IHandler` interfaces, route mapping, validators, result → status mapping) into `artifacts/`; migrate `Message` onto it
- [ ] M3 — **SSE**: typed events with `type`, server-side filters, `Last-Event-ID` replay, `arm events`
- [ ] M4 — **Sessions core**: create/get/list/kill/delete, state machine, queue + slots, FakeProvider; `QUERY` + `queryMethod`; error envelope + multi-status responses
- [ ] M5 — **Gentle restart**: persist session state; server exits, agents survive, new server re-attaches (event replay completes in M6)
- [ ] M6 — **Event bus**: all SPEC event types, filters, replay incl. across restart
- [ ] M7 — **Auth**: fine-grained permissions composed into roles; default roles read-only / operator (start, kill, approve; no configuration) / admin; session-scoped agent tokens; local tokens (A3)
- [ ] M8 — **Approvals**: approval tools, policies (versioned), approve/deny, waiting calls; shell classifier as a **separate low-dependency package** (wrap an existing parser, e.g. tree-sitter-bash / mvdan/sh / bashlex, else from scratch)
- [ ] M9 — **Claude provider** (re-evaluate the current CLI first): real Claude CLI, health, record/replay LLM stub
- [ ] M10 — **Skills & tools** registries + session injection
- [ ] M11 — **Messaging + revive**
- [ ] M12 — **Completions** (optional schema validation, retries)
- [ ] M13 — **Codex provider** (re-evaluate the current CLI first)
- [ ] M14 — **Retention**: 6-month retention, hot/cold tiers, compressed archive
- [ ] M15 — **API versioning** (B3)
