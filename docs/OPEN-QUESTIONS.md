# ARM — Open Questions & Leanings

**How to read this file.** Every entry is a *dated leaning*, not a binding decision. Projects
move, and a leaning from weeks ago may no longer hold. Before relying on one, confirm it with
Oliver. When something changes, rewrite the entry and its date rather than appending history.

References: [`SPEC.md`](SPEC.md), [`PLAN.md`](PLAN.md), [`P0-SPEC-FIRST.md`](P0-SPEC-FIRST.md).

---

## A. Architecture

### A1. .NET version + the `QUERY` verb
- **Leaning (2026-08-14):** stay on .NET 10 minimal API; route `QUERY` via `MapMethods`; keep
  `queryMethod` configurable (SPEC).
- **Note (2026-09-25):** .NET 11 (Nov 2026) adds native `QUERY` and OpenAPI 3.2 SSE output.
  Revisit the bump when it ships.

### A2. Error envelope — open
SPEC mandates `{ "error": { code, message, details } }`. Does Olve.MinimalApi emit this, or do
we need a thin Result→HTTP mapping layer + error-code enum? Verify before M1.

### A3. Auth model — open
SPEC: zero-config local HMAC tokens, session-scoped agent tokens, roles (operator approves).
Scaffold: OIDC/Authentik only. **Leaning (2026-08-14):** pluggable seam (local HMAC default,
OIDC optional); actor derived from the token. Permission model: see PLAN M4.

### A4. Deployment shape
- **Leaning (2026-08-14):** host process (systemd user unit, like `pl`) for V0, so agents
  survive a server restart and get adopted by PID. k8s/multi-replica only when needed (A7).

### A5. Persistence
- **Leaning (2026-08-14):** one EF Core persistence port + a notify port.
  Local: SQLite + NDJSON files + in-process notify (self-contained is a hard requirement).
  Prod: ARM-owned private Postgres + `LISTEN/NOTIFY`. CI exercises both engines.

### A6. Where agents run
- **Leaning (2026-08-14):** `IAgentExecutor` seam (spawn / kill / list / re-attach);
  V0 = `LocalProcessExecutor`. Later: k8s Job executor. **Open:** remote SSH executor
  (run agents on registered machines) — V0 or fast-follow?

### A7. HA / approval-await under multiple replicas
- **Leaning (2026-08-14):** V0 is single-node; build the waiter and event bus behind the notify
  port so Postgres `NOTIFY` can take over later. SSE + REST stays; WebSockets don't help. No
  broker until the `session.text` firehose needs one.

### A8. API definition — code-first vs spec-first
- **Leaning (2026-09-25):** spec-first with TypeSpec (`src/spec/main.tsp`).
  - Backend hand-written (minimal API, AOT), checked by route-coverage + contract tests.
  - Clients via OpenAPI 3.2 → Hey API; CLI is TypeScript on that client, `bun build --compile`.
  - No C# client. Generated output lives only in gitignored `artifacts/`.
  - mise as the single build entry point; one root `package.json`.
- Plan, stages and spike findings: [`P0-SPEC-FIRST.md`](P0-SPEC-FIRST.md). Promote to the
  `olve-api` template only after P0 1.0.

---

## B. Spec TBDs — open

- **B1. Storage layout** — resolve with A5.
- **B2. Config location / filename** — for the host-process deployment (A4).
- **B3. API versioning** — `/api/v1/…` prefix vs header; TypeSpec `@versioned` (P0 0.6).
- **B4. `queryMethod` default** — confirm it stays `"query"` (A1).

---

## C. Scaffold leftovers

- **C1.** Authentik application slugs `olve-arm` / `olve-arm-spa` must be created when the app
  is registered.
- **C3. `Message` example — open:** keep as the P0 test bed; remove when the first real ARM
  entity lands?
