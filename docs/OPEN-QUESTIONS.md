# ARM — Open Questions

Decisions to resolve before/while building V0. Each notes context, options, and a
recommendation where there is one. References are to [`SPEC.md`](SPEC.md) and to the
`Olve.Template.Api` scaffold this repo was generated from.

Grouped as: **A. Template ↔ spec tensions** (where the spec contradicts a scaffold
default), **B. Spec-declared TBDs** (the spec explicitly defers these), and
**C. Scaffold hygiene** (leftovers from generation).

---

## A. Template ↔ spec tensions

### A1. .NET version + the `QUERY` verb
- **Context.** SPEC §"HTTP Method Conventions" assumes **.NET 11** + MVC controllers
  (`[AcceptVerbs("QUERY")]`, CSRF middleware, aspnetcore#67839). The scaffold is
  **.NET 10, minimal API** (no controllers), published **AOT**.
- **Options.**
  1. Bump TFM to .NET 11 and adopt controllers for the search endpoints only.
  2. Stay minimal API and route `QUERY` via `app.MapMethods("/api/…", ["QUERY"], …)`.
  3. Ship `queryMethod: "get"` (default off the custom verb) and revisit later.
- **Also verify:** custom HTTP verb behaviour under **AOT** publish.
- **Recommendation:** (2) — keep minimal API + Olve.MinimalApi result mapping, expose
  `QUERY` via `MapMethods`, keep `queryMethod` configurable per spec. Bump to .NET 11
  only if a concrete feature needs it.

### A2. Error envelope
- **Context.** SPEC §"Error Model" mandates `{ "error": { code, message, details } }`
  with a rich status table (409/410/422/429/503). The scaffold maps errors via
  Olve.Results → Olve.MinimalApi, whose default shape is **not confirmed** to match this
  envelope.
- **Open:** does Olve.MinimalApi emit this envelope, or do we need a custom
  result→HTTP mapping layer + an error-code enum (`APPROVAL_EXPIRED`, …)?
- **Action:** verify the default mapping; if it differs, add a thin mapping layer rather
  than abandoning the Result pattern.

### A3. Auth model
- **Context.** SPEC §"Authentication" wants **zero-config HMAC self-issued tokens**
  (`arm login`), **scoped agent tokens** (`AGENT_RUNTIME_TOKEN`), and **permission tiers**
  (read-only / operator / admin; operator required to approve). The scaffold ships
  **OIDC/Authentik JWT only**.
- **Gap.** OIDC covers the "multi-user / remote" mode, but token *issuance*, agent-scoped
  tokens, and the tier model are all additive.
- **Recommendation:** introduce a pluggable auth seam — local HMAC (default, single-user)
  *or* OIDC (configured backend) — plus a permission/scope model. Actor derived from token
  (SPEC §Auth), never user-supplied.

### A4. Deployment shape — host process vs k8s
- **Context.** SPEC §Restart's V0 model (server exits, **detached agents survive**, new
  server **adopts by PID** + log replay) implies a **host process**. A k8s pod restart is
  a fresh container with no surviving PIDs. The scaffold is **k8s-ClusterIP-first**; the
  spec parks "agents as Kubernetes jobs" as *Future*.
- **Recommendation:** deploy V0 as a **host process** (systemd user unit, à la
  `aoe-serve`/`pl`) while still using the scaffold's build + Olve.Pipelines CD. Decide
  this early — it ripples into A3 (local HMAC makes more sense on a host) and into
  persistence layout below.

### A5. Persistence split (the big one)
- **Context.** The scaffold's `EntityStore<T>` + snapshot persister is whole-snapshot,
  single-store, in-memory-backed. SPEC §Persistence also needs logs, conversations, the
  event NDJSON firehose, **hot/cold tiering, compressed cold archive, 6-month retention**.
- **Proposed split:**
  - **Control-plane metadata → `EntityStore<T>`** (fits well): sessions, approvals,
    policies (versioned, GC'd when unreferenced), tools, skills-metadata, providers,
    idempotency keys (24h TTL). A janitor `BackgroundService` handles GC/TTL.
  - **Log/event/conversation firehose → a separate append/archive subsystem** — beyond
    `EntityStore`, and beyond the `ISnapshotStore` blob port (key→bytes, not
    append/stream/tiered). Needs its own abstraction (hot in-memory buffer + daily NDJSON
    on disk + cold compressed archive queryable by ID).
- **Open:** exact storage layout for the archive tier; whether the event bus buffer and
  the persistence tier share a writer. Note the scaffold's persister safety policy
  (never overwrite good state on load failure) is exactly what ARM's restart/adoption
  reload needs — reuse it for the control-plane stores.

---

## B. Spec-declared TBDs

### B1. Storage layout
SPEC §Persistence: "Storage layout TBD before implementation." Resolve alongside A5.

### B2. Config location / filename
SPEC §Configuration: "Location/filename TBD." Scaffold convention is appsettings +
`dotnet user-secrets`; pick a file path + precedence for the host-process deployment (A4).

### B3. API version strategy
SPEC §"Next Steps" #4: version prefix (`/api/v1/…`) vs negotiated header — undecided.

### B4. `queryMethod` default under our chosen routing
Ties to A1 — if we go `MapMethods("QUERY")`, confirm the default stays `"query"`.

---

## C. Scaffold hygiene (generation leftovers)

### C1. Deploy name ~~is a mouthful~~ — RESOLVED
The kebab transform first produced `olve-agentruntimemanager`; **renamed to `olve-arm`**
across `helm/`, `.pipelines/`, `tools/version.cs`, telemetry service name, auth
authority/audience/client-id, frontend OIDC storage keys, and the `clients/olve-arm-client-ts`
directory. Note: the Authentik application slugs (`olve-arm`, `olve-arm-spa`) must be created
to match when the app is registered.

### C2. `docs/DESIGN.md` — RESOLVED (deleted)
Was template-development docs (about building the Olve.Template.Api ecosystem), noise here.
Removed.

### C3. The `Message` example feature
The scaffold ships a `Message` CRUD example (endpoints, handlers, store, seeder, frontend
`<message-list>`, tests). It's the worked example, not an ARM feature. **Keep as a
pattern reference until the first real ARM entity lands, then remove?**

### C4. .NET 10 SDK not installed locally
Only .NET 8 (8.0.129) is on this machine; the project pins **10.0.200**. Build/test can't
run locally until the .NET 10 SDK is installed. CI is unaffected (uses `dotnet/sdk:10.0`).
