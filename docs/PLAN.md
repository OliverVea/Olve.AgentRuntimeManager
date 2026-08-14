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
  - [ ] A6 — agent execution/hosting model (local process / sandbox / container / k8s job)
  - [ ] B1–B4 — storage layout, config location, API versioning, `queryMethod` default
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

- [ ] Install/pin the .NET 10 SDK so build/test run locally (OPEN-QUESTIONS C4)
- [ ] `dotnet build` clean; `dotnet test` green (unit + integration)
- [ ] Docker image builds (AOT, chiseled) and runs; `/health` responds
- [ ] Helm chart renders (ClusterIP only) with `olve-arm` names
- [ ] OpenAPI `api.json` generates on build
- [ ] C# (Refit) and TS (Kiota) clients generate cleanly and compile
- [ ] Frontend builds (`dist`) and is served from `wwwroot`
- [ ] `arm` CLI project builds and produces a distributable binary (download path like `pl`)
- [ ] `.pipelines/` config validates; build/test/deploy scripts wire the `olve-arm` identifiers
- [ ] Set up hosted project documentation site (mirroring the Olve.Utilities docs setup) and confirm it publishes

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

_To be populated in Phase 3 (dependency-ordered). Each entry gets its own Phase 4 cycle._

- [ ] M1 — …
- [ ] M2 — …
- [ ] M3 — …

## Phase 5 — Finalize

- [ ] All milestone PRs merged; `main` green
- [ ] Everything synchronized & up to date:
  - [ ] `api.json` current; clients (C#/TS) regenerated from it
  - [ ] `OPEN-QUESTIONS.md` decisions all closed
  - [ ] `SPEC.md` / `HLD.md` reflect the built system
  - [ ] Hosted docs published and current
  - [ ] This `PLAN.md` reflects the final state
- [ ] Confirm the deploy path (beta → prod via Olve.Pipelines) works end to end
