# ARM — Project Plan

Task-only plan — no times, dates, or effort estimates. Checkboxes track state.
The **Phase 4** cycle repeats, one milestone at a time, for every milestone defined in
**Phase 3**. "Future" items from [`SPEC.md`](SPEC.md) are out of scope for this plan.

See [`OPEN-QUESTIONS.md`](OPEN-QUESTIONS.md) for the decisions that Phase 1 resolves.

---

## Phase 0 — Repository (done)

- [x] Scaffold `Olve.AgentRuntimeManager` from the `olve-api` template
- [x] Add `docs/SPEC.md` (ARM V2) and `docs/OPEN-QUESTIONS.md`
- [x] Rename deploy identifier to `olve-arm`; align generated client identifiers; drop template `DESIGN.md`
- [x] `git init` + publish public repo (`OliverVea/Olve.AgentRuntimeManager`)

## Phase 1 — Finish the HLD

- [ ] Resolve every open question in `OPEN-QUESTIONS.md` and record the decision inline
  - [ ] A1 — .NET version + `QUERY` routing
  - [ ] A2 — error envelope / result→HTTP mapping
  - [ ] A3 — auth & token model (local HMAC vs OIDC, agent tokens, permission tiers)
  - [ ] A4 — deployment shape (host process vs k8s)
  - [ ] A5 — persistence split (control-plane store vs log/event/conversation firehose)
  - [ ] A6 — agent execution/hosting model (local process / sandbox / container / k8s job / SSH)
  - [ ] A7 — MCP transport + approval-await under distribution/HA (notify port; SSE stays)
  - [ ] A8 — API definition: spec-first via TypeSpec (direction set; proven by P0, see [`P0-SPEC-FIRST.md`](P0-SPEC-FIRST.md))
  - [ ] B1–B4 — storage layout, config location, API versioning, `queryMethod` default

  Resolved directions (see [`OPEN-QUESTIONS.md`](OPEN-QUESTIONS.md) and [`TESTING.md`](TESTING.md)):
  A4 host-process V0; A5 EF Core port — SQLite+NDJSON+in-process notify local / owned private
  Postgres+`NOTIFY` prod; A6 `IAgentExecutor` seam, `LocalProcessExecutor` V0 (SSH executor
  open: V0 vs fast-follow); A7 single-node V0 behind the notify port, no coordination backbone.
- [ ] Write `docs/HLD.md` covering the component architecture:
  - [ ] Session manager & lifecycle state machine
  - [ ] Provider/process supervision (spawn, stream parse, PID adoption)
  - [ ] Event bus / SSE (replay, filtering, in-memory buffer + NDJSON tier)
  - [ ] Approval engine + shell-aware classifier
  - [ ] Persistence tiers (per A5) and restart/adoption reload
  - [ ] Auth/token model (per A3)
  - [ ] CLI ↔ API relationship (`arm` via generated client)
  - [ ] Deployment topology (per A4)
- [ ] Reconcile the HLD against `SPEC.md`; amend the spec where the HLD changes it
- [ ] Review & approve the HLD before implementation begins

## Phase 2 — Build the correct output artifacts

Establish that the scaffold produces every artifact ARM ships, before feature work.

- [ ] **P0 — spec-first toolchain**, staged 0.1 (one POST/GET) → 0.2 (SSE) → … → 1.0; see
  [`P0-SPEC-FIRST.md`](P0-SPEC-FIRST.md). The full ARM API is P1 and builds on it.

- [ ] Install/pin the .NET 10 SDK so build/test run locally (OPEN-QUESTIONS C4)
- [ ] `dotnet build` clean; unit + in-process API tests green (SQLite + FakeProvider)
- [ ] Docker image builds (AOT, chiseled) and runs; `/health` responds
- [ ] Helm chart renders (ClusterIP only) with `olve-arm` names
- [ ] OpenAPI `api.json` generates on build
- [ ] TS (Kiota) client generates cleanly and compiles (no C# client — see A8)
- [ ] Frontend builds (`dist`) and is served from `wwwroot`
- [ ] `arm` CLI project builds and produces a distributable binary (download path like `pl`)
- [ ] Set up hosted project documentation site (mirroring the Olve.Utilities docs setup) and confirm it publishes

Pipeline + testing shape (see [`TESTING.md`](TESTING.md); invoke `ovea-olve-pipelines` before editing `.pipelines/`):
- [ ] Production (parallel): `build-and-package` + `test` (fast, no containers: build/typecheck + unit + in-process API on SQLite + FakeProvider)
- [ ] Processing (sequential): `deploy-beta` → `test-after-beta` (live beta, LLM-free, isolated data) → `deploy`
- [ ] Deploy ARM's **owned private Postgres** via `.pipelines/` (static-cred, ClusterIP, PVC) as part of deploy-beta/deploy
- [ ] Drop the image-building Testcontainers path; `AppFixture` runs in base-URL mode (localhost dev / in-cluster beta Service CI)
- [ ] `.pipelines/` scripts fetch shared `olve-lib.sh` and wire the `olve-arm` identifiers

Test components to build (prerequisite for milestone testing):
- [ ] `FakeProvider` (deterministic scripted provider; used per-push and by `test-after-beta`)
- [ ] Record/replay LLM stub (mock endpoint in provider SSE format, seeded from recorded transcripts)
- [ ] Verify base-URL knobs for the real CLIs (`ANTHROPIC_BASE_URL` / Codex `model_providers` base_url) via context7

## Phase 3 — Milestone breakdown

- [ ] Decompose V0 (from `SPEC.md`, excluding all "Future" items) into milestones
- [ ] Order milestones by dependency
- [ ] Populate the **Milestones** list under Phase 4 with scope + acceptance per milestone

## Phase 4 — Per-milestone execution

Repeat this cycle for **each** milestone below, one at a time, on its own feature branch:

- [ ] Write Gherkin scenarios for the milestone
- [ ] Implement the scenarios as executable tests
- [ ] Run them; confirm they **fail** (red)
- [ ] Implement the feature, turning tests green one after another
- [ ] Call out any missing parts discovered along the way
- [ ] Implement those missing parts
- [ ] Run **all** tests
- [ ] Fix any tests broken by the change
- [ ] Manually test the flow end to end
- [ ] Update docs: `README.md` + hosted project documentation (as in Phase 2)
- [ ] Commit and push
- [ ] Raise a PR

### Milestones

_Draft (P1 = the full ARM API, built on the P0 toolchain). Dependency-ordered; each entry gets
its own Phase 4 cycle. Scope + acceptance per milestone still to be written._

- [ ] M1 — **Sessions core**: create/get/list/kill/delete, state machine, queue + slots, FakeProvider
- [ ] M2 — **Gentle restart**: persist session state; server exits, agents survive, new server adopts by PID (event replay completes in M3)
- [ ] M3 — **Event bus**: all SPEC event types, filters, `Last-Event-ID` replay, NDJSON persistence (incl. replay across restart)
- [ ] M4 — **Auth**: fine-grained permissions (`sessions:create`, `sessions:kill`, `approvals:decide`, …) composed into roles; default roles read-only / operator (functional: start, kill, approve; no configuration) / admin; agent tokens = session-scoped permission set; local HMAC (A3)
- [ ] M5 — **Approvals**: ARM MCP server, policies (versioned), approve/deny, long-poll wait; shell classifier as a **separate low-dependency package** (wrap an existing parser, e.g. tree-sitter-bash / mvdan/sh / bashlex, else from scratch)
- [ ] M6 — **Claude provider**: real Claude CLI, health, record/replay LLM stub
- [ ] M7 — **Skills & tools** registries + session injection
- [ ] M8 — **Messaging + revive**
- [ ] M9 — **Completions** (schema validation, retries)
- [ ] M10 — **Codex provider**
- [ ] M11 — **Retention**: 6-month retention, hot/cold tiers, compressed archive

## Phase 5 — Finalize

- [ ] All milestone PRs merged; `main` green
- [ ] Everything synchronized & up to date:
  - [ ] `api.json` current; TS client regenerated from it
  - [ ] `OPEN-QUESTIONS.md` decisions all closed
  - [ ] `SPEC.md` / `HLD.md` reflect the built system
  - [ ] Hosted docs published and current
  - [ ] This `PLAN.md` reflects the final state
- [ ] Confirm the deploy path (beta → prod via Olve.Pipelines) works end to end
