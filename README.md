# Olve.AgentRuntimeManager

ARM — Agent Runtime Manager: a standalone runtime for LLM agent sessions (see [`docs/SPEC.md`](docs/SPEC.md)). Scaffolded from the `olve-api` template; spec-first (TypeSpec) with a generated backend surface, TS client and CLI.

## Usage

```bash
# Install the template
dotnet new install .

# Create a new project
dotnet new olve-api -n "MyCompany.MyService"
```

## Project Structure

```
src/
├── backend/                                    # .NET — run dotnet commands from here
│   ├── Olve.AgentRuntimeManager.slnx
│   ├── Olve.AgentRuntimeManager/               # API application (minimal API)
│   │   ├── Api/                                # Runtime for the generated API surface (typed responses, error envelope, validation, binding failures, handler check)
│   │   ├── Configuration/                      # Auth, telemetry, JSON, host config
│   │   ├── Events/                             # Event bus + GET /api/events (SSE)
│   │   ├── Sessions/                           # Session runtime: queue + slots, state machine, handlers; Providers/ (fake, Claude)
│   │   ├── Persistence/                        # EF Core + SQLite: ArmDbContext, EfSessionStore, Migrations/
│   │   ├── Health/                             # Health check endpoints
│   │   └── appsettings.json                    # Default configuration
│   ├── Olve.AgentRuntimeManager.UnitTests/     # Unit tests (TUnit + Rocks)
│   ├── Olve.AgentRuntimeManager.ApiTests/      # API behaviour tests over raw HTTP (in-process, or any server via ARM_API_BASE_URL)
│   ├── tools/version.cs                        # CalVer versioning script
│   ├── .config/dotnet-tools.json               # Local tools (dotnet-outdated, dotnet-ef)
│   ├── global.json                             # SDK pin
│   ├── Directory.Build.props                   # Shared build properties (TFM, nullable, etc.)
│   └── Directory.Packages.props                # Central package version management
├── frontend/                                   # Vanilla Web Components + TS frontend (see src/frontend/README.md)
├── cli/                                        # `arm` CLI (TypeScript on the generated client; see src/cli/README.md)
├── codegen/typespec-arm-csharp/                # Our TypeSpec emitter: contract → C# backend surface
│   └── test/                                   # Snapshot tests + conformance/ (the generator's contract-conformance suite, .NET)
├── spec/                                       # API contract (TypeSpec): main.tsp + one file per area — see docs/SPEC-FIRST.md
└── deploy/
    └── vm/                                     # VM deployment: vm-deploy.sh (run on the host), systemd unit, cloud-init, env
.pipelines/                                     # Olve.Pipelines CD config (build, test, deploy beta→prod)
Dockerfile                                      # Multi-stage build (self-contained JIT, chiseled); repo root is the build context
mise.toml                                       # Toolchain pins + tasks (`mise run ci`)
package.json                                    # npm workspace root: TypeSpec + Hey API tooling, frontend, cli
tspconfig.yaml, openapi-ts.config.mjs           # Contract → OpenAPI + C# backend → TS client generation config
artifacts/                                      # Everything generated (gitignored)
```

## Endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/` | No | The SPA (`src/frontend/`), served from `wwwroot` — see [Frontend](#frontend) |
| GET | `/health` | No | Health check, returns 200 |
| GET | `/api/auth-config` | No | Public OIDC settings for the SPA login (authority, client id, scopes) |
| GET | `/api/server-info` | Yes (JWT) | Build version and environment (`beta`/`prod`) from the deploy; both `null` on a local run |
| POST | `/api/sessions` | Yes (JWT) | Create a session; returns its id: 201 started, 202 queued, 503 queue full; `Idempotency-Key` honoured |
| POST | `/api/sessions/search` | Yes (JWT) | Search sessions (filters in the body), newest first |
| GET | `/api/sessions/{id}` | Yes (JWT) | Get a session |
| POST | `/api/sessions/{id}/kill` | Yes (JWT) | Kill a queued or working session (`{ "caller": "…", "reason": "…" }`); 409 if it already ended |
| DELETE | `/api/sessions/{id}` | Yes (JWT) | Delete a session that has ended; 409 otherwise |
| GET | `/api/events?event=<a,b>&exclude_event=<a,b>` | Yes (JWT) | Server-sent events: `session.created` / `.queued` / `.started` / `.completed` / `.failed` / `.killed` (…), heartbeats; `Last-Event-ID` replays missed events |
| GET | `/openapi/v1.json` | No | OpenAPI spec |

The JSON API lives under `/api/` so the SPA can own the site root; `/health` stays at the root
for health probes. Unmatched non-API GETs fall back to `index.html` for SPA client routing.

The `/api` endpoints are generated from the contract (see [Client Generation](#client-generation)):
the app implements one generated `I…Handler` per operation and opts operations out of auth in
`Program.cs`. Every error is the envelope `{ "error": { code, message, details } }`.

Sessions (`Sessions/`) are stored in SQLite (see [Persistence](#persistence)): `SessionManager`
keeps a FIFO queue in front of `Sessions:TotalSlots` slots, moves sessions through the state machine
(`SessionLifecycle`), kills them at their timeout, and publishes a lifecycle event per change.
Agents run through the `IAgentProvider` seam. `fake` runs no LLM and follows `fake:` directives in
the prompt (`fake:sleep=2s`, `fake:hang`, `fake:exit=3`, `fake:fail=…`, `fake:down=unreachable:1`; see
`FakeScript`). `claude` runs Claude Code (`Providers/Claude/`): one `claude -p` process per session,
spoken to in its `stream-json` protocol, locked down (no built-in tools, none of the machine's
settings, plugins, MCP servers, connectors, skills or memory, and only an allowlist of environment
variables), with the ARM session id as Claude's session id (a retry gets a new one). It answers one prompt with one turn; its
output (the agent's answer included) is kept in `<WorkRoot>/<session id>/output.jsonl` until the
conversation is in the API (M5b). It uses the machine's Claude Code login (or
`CLAUDE_CODE_OAUTH_TOKEN`); tests run it against a stub CLI replaying recorded output.

A provider that refuses an agent before it did anything (Claude: the API's 401/403, a usage limit's
429, a 5xx/529 or no connection) pauses, and `GET /api/providers/health` (`arm provider health`)
says why and until when: `limited` until the limit resets, `unreachable` for a wait that doubles
(`Sessions:ProviderBackoff` up to `Sessions:ProviderMaxBackoff`) before one queued session tries
again, `unauthorized` until a restart. New sessions still queue; other providers' sessions pass
them. The refused session goes back to the head of the queue (`working → queued`, with the
error) and fails after `Sessions:ProviderRetries` retries; waiting out a known limit reset uses
none. Health is in memory only, and every change is a `provider.health` event.

`GET /api/events` (`Events/`) streams every session change as SSE (`arm events` tails it). Each
event's JSON data carries `type` (= the SSE event name), `at` and its subject id; every event but
`heartbeat` has a monotonic id. Handlers publish on the in-process `EventBus`, which keeps the
last `Events:ReplayCapacity` events in memory for `Last-Event-ID` replay (not across restarts
yet). Filters are enforced server-side: `event` keeps only the listed names (`session.*` matches
a namespace), `exclude_event` then drops names; an unknown name is a 400, and heartbeats (sent
on connect, then every `Events:HeartbeatInterval`) always pass.

## Build & Test

Run from `src/backend/` (the solution root):

```bash
# Restore and build
dotnet restore
dotnet build

# Unit + API tests (default; the build needs Node for `npm run spec`, see below)
dotnet test

# API tests only, against a running server instead of in-process (see below)
ARM_API_BASE_URL=http://localhost:5000 dotnet test --project Olve.AgentRuntimeManager.ApiTests
```

The API project's build runs `npm run spec` from the repo root (incremental: skipped while
`artifacts/` is newer than the spec, `tspconfig.yaml` and the emitter) and compiles the generated
`artifacts/generated/backend/*.g.cs`; `-p:SkipSpecGen=true` compiles already-generated files
without Node (the Dockerfile does this).

Testing is split by responsibility:

- **Contract conformance is the generator's job**, tested once in the tooling, not per service:
  `src/codegen/typespec-arm-csharp/test/conformance/` compiles a fixture spec's generated C# with
  the runtime in `Olve.AgentRuntimeManager/Api/`, hosts it with trivial handlers, and checks over
  HTTP that every spec operation is routed (and every `/api` endpoint is in the spec), every
  response validates against the fixture's OpenAPI schema for its status (objects closed, so
  undeclared fields fail), `ArmResults` honours the declared statuses, binding failures produce
  the declared error body, and a missing handler stops startup. It runs in `npm run codegen:test`
  / `mise run codegen:test`.
- **API tests** (`Olve.AgentRuntimeManager.ApiTests`) are ordinary behaviour tests of ARM's
  implementation over raw HTTP (no generated client): statuses, bodies and messages per
  operation, auth (401 on writes, reads anonymous), the handler-registration check. One suite,
  selectable target (`ApiTarget`):
  - default: in-process (`WebApplicationFactory`) — what `dotnet test` and `mise run ci` run;
  - base-URL mode: set `ARM_API_BASE_URL` to test any running server (a local `dotnet run`, the
    Docker image, later live beta). Tests that mint a token need the server's auth settings in
    `ARM_API_SIGNING_KEY`, `ARM_API_ISSUER` and `ARM_API_AUDIENCE` and are skipped without them;
    tests that inspect the host (DI, configuration) run in-process only.
  - `mise run api:image` builds the `Dockerfile` image, starts it with a test signing key, and
    runs the suite against it in base-URL mode (needs Docker).

Test execution is controlled by MSBuild properties:
- `RunUnitTests=false` skips unit tests
- `RunApiTests=false` skips API tests

## Running

```bash
# Local (from src/backend/)
dotnet run --project Olve.AgentRuntimeManager
```

## Deployment (GitOps)

The repo deploys via [**Olve.Pipelines**](https://github.com/OliverVea/Olve.Pipelines).
`.pipelines/config.yaml` is the **single source of truth**; the pipeline is bound to this repo, so
**pushing to `main` redeploys automatically**.

- **Production steps (parallel):** `build-and-package` (Kaniko → image tarball + `src/deploy/vm`),
  `check` (`mise run ci`: emitter conformance, backend unit + API tests, frontend, CLI) and
  `claude-code` (the latest Claude Code release for the agents: manifest signature and checksum
  verified, lockdown flags checked, the binary staged in the bundle). A failure gates everything after.
- **Processing steps (sequential):** `deploy-beta` (ends with one real `claude` session through
  ARM, `src/deploy/vm/claude-check.sh`, to confirm the provider, login and lockdown) → `test-after-beta` (the API test
  suite against live beta) → `deploy` (prod).
- **Secrets by name only** (`GITHUB_TOKEN`, `SSH_PRIVATE_KEY`, `CLAUDE_CODE_OAUTH_TOKEN_BETA`,
  `CLAUDE_CODE_OAUTH_TOKEN_PROD`, `ARM_BETA_OIDC_CLIENT_SECRET`); values live in the pipeline's k8s secret. The Claude Code
  tokens come from `claude setup-token` (valid one year; set with `pl secret set`); keep the beta
  one for rare manual checks. Step scripts source the shared
  [`olve-lib.sh`](https://github.com/OliverVea/Olve.Pipelines/blob/main/.pipelines/scripts/olve-lib.sh).

**Where it runs.** ARM runs in **libvirt VMs** on the homelab host (`olve-arm-beta`,
`olve-arm-prod`), not in Kubernetes — agents must survive server restarts. Each deploy step copies
the image tarball and `src/deploy/vm/` to the host and runs `vm-deploy.sh`, which:

1. ensures the VM (Ubuntu 24.04 cloud image + cloud-init, fixed IP on libvirt's `default` network,
   autostart) — the first deploy creates it;
2. extracts the published app from the image and installs it as
   `/opt/olve-arm/releases/<version>` with a systemd service (`KillMode=process`, so agent
   processes survive a server restart);
3. installs the bundle's Claude Code as `/opt/olve-arm/claude/<version>` (uploaded once per
   version) and links it into the release as `claude`, so rolling back a release rolls back its
   Claude Code too;
4. writes the config (`src/deploy/vm/env.{beta,prod}`, the prod OTLP secret from the cluster, the
   Claude Code token from the pipeline secret, passed over stdin) and the Authentik CA;
5. ensures a host relay (`100.100.117.17:18792` beta, `:18791` prod → VM:5000).

**Routing** lives in [`Olve.Homelab`](https://github.com/OliverVea/Olve.Homelab): `arm-beta.ovea.pro`
and `arm-private.ovea.pro` (Tailscale-private) use `hostEndpoint` to target the relay — pods
can't open new connections into libvirt's NAT network directly. Authentik applications `olve-arm`
and `olve-arm-spa` are defined in `Olve.Authentik`.

Inspect runs, jobs, and logs with the **`pl` CLI** (`pl pipeline list`, `pl job logs <id>`,
`pl binding status <id>`). The [`ovea-olve-pipelines`](https://github.com/OliverVea/Olve.Pipelines)
skill and the instance's `/docs` (served at
[`pipelines-private.ovea.pro`](https://pipelines-private.ovea.pro), beta at `pipelines-beta.ovea.pro`)
are the authoritative reference for the config schema, promotion gates, and the deploy model — start
there rather than re-deriving it.

## Configuration

Sources in priority order (highest wins):

1. CLI args (`--Port 9090`)
2. User secrets (`dotnet user-secrets set "Key" "value"`)
3. Environment variables
4. `appsettings.{Environment}.json`
5. `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Host` | `localhost` | Listen address |
| `Port` | `5000` | Listen port |
| `Auth:Authority` | `https://auth.ovea.pro/...` | OIDC authority (Authentik) |
| `Auth:Audience` | `olve-arm` | JWT audience |
| `Auth:SigningKey` | _(null)_ | Local HS256 key (bypasses OIDC, for dev) |
| `OpenTelemetry:Endpoint` | `https://otel.ovea.pro` | OTLP endpoint (null = disabled) |
| `ConnectionStrings:Arm` | _(a file in the user's local data folder)_ | The SQLite database, e.g. `Data Source=/var/lib/olve-arm/arm.db` |
| `Events:HeartbeatInterval` | `00:00:30` | Heartbeat period of `GET /api/events` connections |
| `Events:ReplayCapacity` | `1000` | Recent events kept for `Last-Event-ID` replay (and how far a connection may lag) |
| `Sessions:TotalSlots` | `10` | Sessions that run at once; more are queued (202) |
| `Sessions:MaxQueueSize` | `200` | Sessions that may wait for a slot; more are a 503 `QUEUE_FULL` |
| `Sessions:IdempotencyWindow` | `1.00:00:00` | How long an `Idempotency-Key` replays its original response |
| `Sessions:ProviderRetries` | `2` | Retries of a session its provider refused (outage, bad credentials), then it fails |
| `Sessions:ProviderBackoff` | `00:01:00` | How long an unreachable provider waits before one session tries again; doubles per failed try |
| `Sessions:ProviderMaxBackoff` | `00:15:00` | The longest that wait gets |
| `Providers:Fake:Enabled` | `true` | Whether the `fake` provider exists; `false` in prod |
| `Providers:Fake:Delay` | `00:00:02` | How long a fake agent runs unless its prompt says otherwise (`fake:sleep=…`) |
| `Providers:Claude:Command` | `claude` | The Claude Code executable |
| `Providers:Claude:WorkRoot` | `olve-arm/sessions` in the user's local data folder | Each session's folder: working directory (`work/`) and raw output |
| `Providers:Claude:ConfigDirectory` | *(Claude Code's default, `~/.claude`)* | `CLAUDE_CONFIG_DIR` for the agents |
| `Providers:Claude:ExitGrace` | `00:00:10` | How long an agent may take to exit after its turn before it's stopped |

### Persistence

Sessions live in SQLite through EF Core (`Persistence/`; OPEN-QUESTIONS A5): `ArmDbContext`, and
`EfSessionStore` behind the `ISessionStore` port. `SessionManager` stores every change before it
changes memory or publishes the event; it keeps queued and working sessions in memory too (queue
positions aren't stored) and reads ended ones from the database.

- **Where:** `ConnectionStrings:Arm`. Unset, a local run uses `arm.db` in the user's local data
  folder (`~/.local/share/olve-arm/` on Linux), never the repo. The VMs use
  `/var/lib/olve-arm/arm.db` (systemd's `StateDirectory`, outside the release folders a deploy
  replaces). The API tests give each in-process host a database of its own.
- **Schema:** EF Core migrations in `Persistence/Migrations/`, applied at startup before the
  server listens. They are generated code but committed: they're the schema's history, which
  can't be regenerated. Add one after changing the model (from `src/backend/`):
  `dotnet ef migrations add <Name> --project Olve.AgentRuntimeManager --output-dir Persistence/Migrations`
  (`dotnet tool restore` first; `ArmDbContextDesignFactory` builds the context without the app).
- **Restart:** the new server kills the sessions that were working (source `system`, "ARM
  restarted; the agent was lost.") and queues the queued ones again in their order, until gentle
  restart (M5a) re-attaches running agents.
- Not stored yet: the event replay buffer (M6) and `Idempotency-Key`s (a retried create after a
  restart creates a new session).

## Client Generation

The contract is `src/spec/` (TypeSpec; `main.tsp` imports one file per area). Everything generated lives in the gitignored
`artifacts/`, never in source folders:

```bash
npm run spec       # src/spec/main.tsp → artifacts/spec/openapi.json + artifacts/generated/backend/*.g.cs
npm run generate   # … then → artifacts/clients/ts (Hey API)
npm run codegen:test   # emitter snapshot tests (UPDATE_SNAPSHOTS=1 to accept changes) + conformance suite (dotnet)
```

The frontend and CLI import the client as `@arm/client` and regenerate it as part of their own
builds. The **backend surface** is generated by our own emitter,
[`src/codegen/typespec-arm-csharp`](src/codegen/typespec-arm-csharp) (plain ESM): records per
model (request shapes split by visibility, e.g. `WidgetWritable`), discriminated unions, and per
operation a request record, a **response union** with one variant per declared status
(`SessionsKillResponse.Ok | .NotFound | .Conflict …`) and `I…Handler : IArmHandler<Req, Response>`;
plus `MapArmApi()` with routes, `.WithArmValidation` (from `@maxLength` etc.) and declared
statuses, a JSON source-gen context and `ArmApi.HandlerTypes`. A handler returns a variant, so an
undeclared status doesn't compile. Hand-written runtime pieces live in
`Olve.AgentRuntimeManager/Api/`: the error envelope (`ArmErrorEnvelope`), `ArmValidation` and
`ArmBindingFailures` (both answer `INVALID_REQUEST`), the SSE result, and `UseArmApi()`, which
refuses to start if a handler is unregistered. The API build compiles the spec itself. The emitter's conformance suite proves the
generated surface matches the spec (see [Build & Test](#build--test)). There is no C# client: the
backend tests speak raw HTTP. See [`docs/SPEC-FIRST.md`](docs/SPEC-FIRST.md).

Run everything the pipeline runs with `npx mise run ci` (mise pins node + dotnet; `npx mise
tasks` lists the tasks).

## Frontend

`src/frontend/` is the template's companion UI: a no-framework, **vanilla Web Components** app in
**TypeScript**, consuming the API through the generated Hey API client. It ships a
read-only `<session-list>` (the newest sessions, kept live from the event stream), proving the
client-gen → component → API loop end to end.

The stance is deliberate (DESIGN §2): standalone custom elements, ES modules, and a shared
`BaseElement` that provides ergonomics only — **explicit `render()`, no automatic
re-rendering**. A component that outgrows this can `npm i lit` and switch its own base to
`LitElement` per-component; auto-rerender is always opt-in, never the baseline.

It's served **same-origin**: the Dockerfile's Node stage builds `src/frontend/dist` into the app's
`wwwroot`, so the deployed API serves the SPA at `/` and the JSON API at `/api/` (one host, no
CORS). Locally you run it on Vite instead, which proxies `/api` to the backend:

```bash
cd src/frontend && npm install && npm run dev    # proxies /api to the API (VITE_API_TARGET)
```

See [`src/frontend/README.md`](src/frontend/README.md) for the layout, run/build commands, auth for
writes, and how the API client is generated.

## Versioning

The `src/backend/tools/version.cs` script computes CalVer versions (run from `src/backend/`):

```bash
# Local development
dotnet run tools/version.cs
# version=0.0.0-dev+cb9a99b

# CI (pass run number from GitHub Actions)
dotnet run tools/version.cs -- --ci --run-number 42
# version=2026.3.28.42+cb9a99b

# With runtime identifier for artifact naming
dotnet run tools/version.cs -- --ci --run-number 42 --rid linux-x64
# artifact-name=olve-arm-2026.3.28.42+cb9a99b-linux-x64
```

## CI

This template does not include a CI workflow — the actual workflow should live in the service's deployment repo. Below are examples to copy and adapt.

### Example: PR workflow

```yaml
# .github/workflows/pr.yml
name: PR

on:
  pull_request:
    branches: [main]

env:
  DOTNET_VERSION: 10.0.100

jobs:
  build-and-test:
    name: Build and test
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/backend
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-restore --no-build -c Release
```

### Example: Push to main workflow

```yaml
# .github/workflows/push-main.yml
name: Push to main

on:
  push:
    branches: [main]

env:
  DOTNET_VERSION: 10.0.100

jobs:
  build-and-test:
    name: Build and test
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/backend
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-restore --no-build -c Release

  version:
    name: Compute version
    needs: build-and-test
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/backend
    outputs:
      version: ${{ steps.version.outputs.version }}
      artifact-name: ${{ steps.version.outputs.artifact-name }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
      - name: Compute version
        id: version
        run: |
          dotnet run tools/version.cs -- --ci --run-number ${{ github.run_number }} \
            | tee -a "$GITHUB_OUTPUT"
```

## Architecture & References

Each major component, what it does in the template, and the full set of links (docs, source,
running instance/tooling, and the Claude Code skill that knows the model).

| Component | Role in the template | Docs | GitHub | Instance / tooling | Skill |
|---|---|---|---|---|---|
| **Olve.Utilities** stack (Results, Validation, Utilities) | Baked-in error handling, validation, `Id<T>`/`EntityStore<T>` primitives | [docs site](https://olivervea.github.io/Olve.Utilities/) | [OliverVea/Olve.Utilities](https://github.com/OliverVea/Olve.Utilities) | NuGet | *(none yet — gap)* |
| **Olve.Pipelines** | GitOps CD — builds & deploys this repo via `.pipelines/` (see [Deployment](#deployment-gitops)) | in-repo `docs/setup/`, served at `/docs` + `llms.txt` | [OliverVea/Olve.Pipelines](https://github.com/OliverVea/Olve.Pipelines) | [`pipelines-private.ovea.pro`](https://pipelines-private.ovea.pro), beta `pipelines-beta.ovea.pro`, hooks `pipelines-hooks.ovea.pro`; **`pl` CLI** via `GET /download/{asset}` | `ovea-olve-pipelines` |
| **Olve.Homelab** | Edge chart that owns all Ingress; public exposure is registered there, not in this chart | — | [OliverVea/Olve.Homelab](https://github.com/OliverVea/Olve.Homelab) | — | — |
| **TUnit · Rocks · TypeSpec · Hey API · mise** | Tests, AOT mocking, API contract, TS client generation, toolchain + tasks | see per-library links below | — | — | — |

Per-library documentation:

- [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — Functional result types for non-throwing error handling
- [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — Fluent input validation built on Olve.Results
- [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — Meta-package bundling utility libraries including identifiers, collections, and graph types
- [TUnit](https://tunit.dev/docs/intro) — Test framework (not xUnit/NUnit). Uses `await Assert.That(...)` fluent syntax
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — Source-generated mocking library for AOT-compatible test doubles
- [TypeSpec](https://typespec.io/docs/) — API contract language
- [Hey API](https://heyapi.dev/) — OpenAPI → TypeScript client generator
- [mise](https://mise.jdx.dev/) — toolchain pinning and task runner
