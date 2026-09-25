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
- **Update (2026-09-25):** .NET 11 (Nov 2026) supports `QUERY` natively *and* emits OpenAPI 3.2
  with SSE `itemSchema` from `TypedResults.ServerSentEvents` (useful for the A8 conformance
  check). That's two concrete features: revisit the bump when it ships.

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
  this early — it ripples into A3 (local HMAC makes more sense on a host), into
  persistence layout below, and is tightly coupled with **A6** (where agents run).
- **Resolution (V0):** **host process** (systemd user unit, like `pl`/`aoe-serve`).
  Revisit in-cluster + multi-replica only when distribution is genuinely needed — the HA
  implications are in **A7**. A4 + A6 + A3 form one coupled cluster: *host-process ARM +
  local agents + local HMAC* (V0) vs *in-k8s ARM + k8s-Job agents + OIDC* (distributed).

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
- **Resolution.** One **EF Core persistence port** + a **notify port**, two impls each,
  selected by environment:
  - **Local (self-contained — a hard requirement):** SQLite (control-plane) + NDJSON files
    on disk (firehose) + **in-process** notify. No daemon, no container — `arm server
    start` and go; keeps `arm` feeling like `pl`.
  - **Prod:** ARM **owns a private single-replica Postgres**, deployed by its own
    `.pipelines/` (static-cred, ClusterIP, PVC) + `LISTEN/NOTIFY`. Deliberately **not** the
    shared homelab Postgres — a private, static-cred dep avoids the shared-dependency chain
    the self-bootstrap principle exists to kill.
  - **Two engines, one port.** Guardrail against dialect drift: CI exercises both — SQLite
    in-process pre-deploy, Postgres live via `test-after-beta` (see [`TESTING.md`](TESTING.md)).
    Keep the persistence layer to the common SQL subset; PG-only bits (`NOTIFY`, JSONB)
    live behind the notify/store port with an in-process/SQLite equivalent.
  - SQLite is kept for local *because* self-contained local is required; it is **not**
    carried into prod. Cross-replica/HA specifics are in **A7**.

### A6. Agent execution / hosting model
- **Context.** The spec's V0 implies agents are **local subprocesses** of the server:
  the provider interface is `spawn(session) → childProcess` with `buildCwd`/`buildEnv`/
  `kill`, and SPEC §Restart adopts survivors **by PID**. SPEC §Queue's memory gate
  (block spawn when host RAM < 2 GB) is a single-host assumption. SPEC §Restart lists
  "agents as **Kubernetes jobs**" as *Future*, and SPEC §Auth defers "full uid-level
  isolation." So *where agents run* is under-specified beyond "local process."
- **Options.**
  1. **Local subprocess on the ARM host** (spec V0). Simplest; PID adoption works; fits a
     host-process deployment (systemd, like `aoe-serve`/`pl`). Isolation is the **approval
     + path policy**, not the OS — agents share the host uid/fs.
  - **1b. Local subprocess + OS sandbox** (cgroups + bubblewrap/nsjail). Same model, adds
     fs/resource isolation without a container runtime.
  2. **Per-session container** (Docker/Podman on the host). Fs/uid/resource isolation +
     reproducible per-session toolchains; adoption by container id; needs a runtime +
     image management.
  3. **Kubernetes Job/Pod per session** (spec's *Future* "distributed mode"). Strong
     isolation, scheduling, quotas; fits the homelab k3s cluster; adoption via the k8s API
     + pod logs (not PID); ARM needs RBAC to create Jobs. The memory gate/queue partly
     defer to the scheduler.
  4. **MicroVM** (Firecracker/Kata). Strongest multi-tenant isolation; overkill for V0.
  5. **Remote subprocess over SSH** — run the agent in a folder on a registered machine ARM
     holds an SSH key for. The executor spawns the CLI *and* runs `approved_bash`/file-ops on
     the **target host, in the target `cwd`**, with `secretEnv` injected into the remote
     subprocess. Requires a new **host/target registry** (`{name, ssh user@host, key ref,
     default cwd, allowed paths}`) and a **per-session target selector** (`--target`/`--cwd`;
     default `local`). MCP reachability via the tailnet (agent calls back to ARM) or an SSH
     reverse tunnel. Adoption: launch under `systemd-run`/`nohup`/tmux so it survives the SSH
     channel + an ARM restart, re-attach by tailing the remote log. Security boundary = the
     approval/path policy, now over the remote fs. ("ARM as a dispatcher across your fleet.")
- **Coupling.** Tied to **A4**: {ARM as host process + local/container agents} vs {ARM in
  k8s + k8s-Job agents}. Local subprocess *inside* a k8s pod is fragile — a pod restart
  kills every agent, breaking survive-and-adopt. The **adoption mechanism** (PID vs
  container-id vs k8s-API) is a function of this choice, as is the isolation roadmap (§Auth).
- **Recommendation:** V0 = **local subprocess on a host-process deployment** (matches the
  spec, PID adoption, homelab-friendly), but introduce an **`IAgentExecutor` seam** now —
  `spawn / kill / list-running / re-attach` — so container and k8s-Job executors are
  pluggable later without touching the session manager or providers (same pattern as
  `ISnapshotStore` for storage). Providers stay "*what* CLI + args"; the executor owns
  "*where/how* it runs." Consider **1b** as the near-term isolation story.
- **Resolution.** Adopt the **`IAgentExecutor` seam** (`spawn / kill / list-running /
  re-attach` + a uniform log stream). V0 ships **`LocalProcessExecutor`** (host process, PID
  adoption). `KubernetesJobExecutor` when distribution is real. The **remote SSH executor**
  (option 5) is a strong candidate feature — **open: V0 or fast-follow?** — and introduces
  the host-registry + target-selector surface above (neither exists in the spec today).
  `approved_bash` executes wherever the executor runs (follows the executor, not "whatever
  replica happened to hold the call").

### A7. MCP transport + approval-await under distribution / HA
- **Context.** Approvals are a *blocking, stateful* wait: the agent's `approved_bash` MCP
  call is held open while a human decides (defer-and-resume), then ARM executes and returns
  stdout. On one node this is an in-memory waiter resolved by the `decide` handler in the
  same process — correct and simple. (`approved_bash` runs as a subprocess of the ARM MCP
  server — so "where it executes" also follows the executor, see A6.)
- **Where it breaks with >1 replica behind an LB.** Three single-node assumptions:
  (1) the **waiter is in-memory** — the `decide` can land on another replica; (2) the
  **event bus + replay are per-node** — in-memory buffer + local NDJSON; (3) the **MCP call
  is one held-open socket** — dies with its replica and fights LB idle timeouts.
- **HA fix — uses the DB you already have, not a broker.**
  (1) waiter → **poll the shared approvals row** / `LISTEN/NOTIFY`, not an in-memory TCS;
  (2) fan-out + replay → shared store (the `Last-Event-ID` replay already reads a log — point
  it at shared storage; `NOTIFY` or poll for cross-replica delivery);
  (3) MCP → **bounded long-poll + reconnect + resume-by-`approvalToken`** (single-use,
  HMAC-bound → idempotent; a re-poll after a replica dies lands elsewhere and reads the same
  row). Discipline: **no authoritative wait/event state in per-replica memory or local disk.**
- **Transport.** Keep **SSE + REST** (the spec's choice). **WebSockets do not help** —
  cross-replica delivery is identical; the reverse channel is already REST; WS only adds
  duplex you don't need + its own LB-affinity headaches.
- **Load balancing.** Session affinity (pin a session's MCP + approvals to one replica) buys
  *throughput* but not *availability* (that replica is a SPOF for its sessions). True HA =
  stateless replicas over shared state (above); affinity then optional.
- **When a broker (Redis/NATS) earns its place.** Only the high-rate `session.text` firehose
  at scale — Postgres alone is fine at homelab/few-replica scale. Deferrable.
- **Resolution.** V0 is single-node (A4 host process) → the in-memory waiter + in-process
  bus is correct. Build it **behind the notify port (A5)** so the SQLite/in-process impl
  becomes Postgres/`NOTIFY` when multi-replica is real. **No coordination backbone in V0.**

### A8. API definition — code-first vs spec-first
- **Context.** The scaffold is **code-first**: `api.json` is generated from the minimal-API
  endpoints on build (`Microsoft.Extensions.ApiDescription.Server`), then Refitter/Kiota
  generate clients from it. SPEC §"Next Steps" wants an OpenAPI document + JSON Schemas +
  contract tests as the *first* deliverable — i.e. **API-first**. The `arm` CLI (every SPEC
  command table pairs `arm …` with an endpoint) should be largely generated from the same
  source.
- **Options.**
  1. Keep code-first (spec is whatever the server emits).
  2. Hand-written OpenAPI YAML as source — no toolchain, but verbose and poor for SSE.
  3. **TypeSpec** (`.tsp`) as source → emits OpenAPI 3.2 (incl. typed SSE events via
     `@typespec/sse`/`@typespec/events`), JSON Schema, and (later) `.proto`.
  4. Protobuf + proto3 JSON mapping / gRPC JSON transcoding — protobuf-flavoured JSON, weak
     SSE story, AOT doubtful.
- **Verified (scratch spike, TypeSpec 1.16, 2026-09-25):**
  - OpenAPI **3.2** output carries per-event SSE schemas (`itemSchema` + `oneOf` on `event`
    const); **3.1 silently drops them** → emit 3.2.
  - Refitter 2.0 and Kiota 1.32 **both accept the 3.2 document** and honour visibility
    (read-only `id`, write-only `secretEnv` excluded from the read model). Neither types the
    SSE stream: Refitter emits `Task Events(...)`, Kiota warns the event union has no
    discriminator. → SSE client consumption is ours to write/generate.
  - `@typespec/http-server-csharp` is **not usable for the server**: emits MVC controllers
    (not AOT, scaffold is minimal API), collapses `201 | 202 | error` to `Ok(...)` with
    anonymous `Model0`, ignores visibility (would put `secretEnv` on the read model), mutable
    classes without `required`, and the generated code **fails to compile** (argument-order
    mismatch between interface and controller; broken ctor name on generic error models).
  - No `QUERY` verb decorator in `@typespec/http` (OpenAPI 3.2 itself supports `query`).
- **Direction (pending P0):** **option 3 — TypeSpec as the single source.** TypeSpec does not
  implement the server: the backend stays hand-written minimal API + Olve.Results, and
  conformance is enforced by (a) a **route-coverage test** (every spec operation's method +
  path is mapped, via `EndpointDataSource`) and (b) **contract tests** validating live
  responses against the emitted schemas (SPEC Next Steps #2) — not by diffing OpenAPI
  documents. Server DTOs are hand-written. **No custom TypeSpec emitter**: clients come from
  off-the-shelf generators and the CLI is hand-written on the generated client. WebSocket/binary (SPEC *Future*, and
  per A7 not needed) would reuse the same models via the protobuf emitter.
- **Couples to:** A1/B4 (`QUERY` + `queryMethod` — one spec operation, three routings; P0
  must solve it), A2 (error envelope modelled once as `@error` models), B3 (TypeSpec
  `@versioned`).
- **No C# client (decided).** The Refitter/Refit client's only consumer was the integration
  tests; they now speak raw HTTP (they test the wire contract, which a typed client masks). The
  `arm` CLI is TypeScript on the generated TS client (below).
- **Generated output is a build artifact.** OpenAPI documents (and generated clients) go to the
  gitignored `artifacts/`, never source folders; `src/spec/` holds only `main.tsp`. The backend
  currently writes `artifacts/backend/api.json`.
- **Clients: TypeSpec → OpenAPI 3.2 → Hey API (decided, replaces Kiota).** Researched and spiked
  2026-09-25: Hey API reads 3.2, honours read-only, has real SSE streaming (reconnect,
  `Last-Event-ID`); it ignores `itemSchema`, so event payloads carry a `type` discriminator.
  `@typespec/http-client-js` (direct TS from TypeSpec) is preview and was buggy on our spec.
  Python/Bash clients come from the same artifact if needed.
- **CLI: TypeScript on the generated client, compiled with `bun build --compile`** to a
  standalone binary (~81 MB, libc only). Spiked.
- **Build orchestration: mise (decided).** Root `mise.toml` pins node/dotnet/bun and defines
  `spec`/`build`/`test`/`ci` tasks that delegate to each project's native build; each native
  build owns its link to `main.tsp`. One root `package.json` (npm workspaces + TypeSpec tooling).
  Same entry point for humans, Claude, and CI. Nx rejected as JS-centric and heavy; just/Taskfile
  lack toolchain pinning.
- **Exit criteria / plan:** [`P0-SPEC-FIRST.md`](P0-SPEC-FIRST.md). Promote to the
  `olve-api` template only after P0 1.0.

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

### C4. .NET 10 SDK not installed locally — RESOLVED
Only .NET 8 (8.0.129) was on this machine; the project pins **10.0.200**. As of 2026-09-25
`dotnet --list-sdks` shows 10.0.200 installed.
