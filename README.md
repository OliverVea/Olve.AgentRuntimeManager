# Olve.AgentRuntimeManager

A .NET 10 minimal API service template. Install with `dotnet new` and scaffold a full solution with auth, telemetry, Helm chart, and client generation.

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
│   │   ├── Api/                                # Runtime for the generated API surface (ArmResults, binding failures, handler check)
│   │   ├── Configuration/                      # Auth, telemetry, JSON, host config
│   │   ├── Messages/                           # Message CRUD example feature
│   │   ├── Stores/                             # EntityStore snapshot persistence (promotion-shaped)
│   │   ├── Health/                             # Health check endpoints
│   │   └── appsettings.json                    # Default configuration
│   ├── Olve.AgentRuntimeManager.UnitTests/     # Unit tests (TUnit + Rocks)
│   ├── Olve.AgentRuntimeManager.ApiTests/      # API behaviour tests over raw HTTP (in-process, or any server via ARM_API_BASE_URL)
│   ├── tools/version.cs                        # CalVer versioning script
│   ├── .config/dotnet-tools.json               # Local tools (dotnet-outdated)
│   ├── global.json                             # SDK pin
│   ├── Directory.Build.props                   # Shared build properties (TFM, nullable, etc.)
│   └── Directory.Packages.props                # Central package version management
├── frontend/                                   # Vanilla Web Components + TS frontend (see src/frontend/README.md)
├── cli/                                        # `arm` CLI (TypeScript on the generated client; see src/cli/README.md)
├── codegen/typespec-arm-csharp/                # Our TypeSpec emitter: contract → C# backend surface
│   └── test/                                   # Snapshot tests + conformance/ (the generator's contract-conformance suite, .NET)
├── spec/main.tsp                               # API contract (TypeSpec) — see docs/SPEC-FIRST.md
└── deploy/
    └── helm/                                   # Helm chart for Kubernetes (ClusterIP Service + SLO)
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
| GET | `/api/messages?page=<n>&pageSize=<n>` | No | List messages (paginated, 1-based) |
| POST | `/api/messages` | Yes (JWT) | Create a message (`{ "text": "…" }`) |
| PUT | `/api/messages/{id}` | Yes (JWT) | Update a message (`{ "text": "…" }`) |
| DELETE | `/api/messages/{id}` | Yes (JWT) | Delete a message |
| GET | `/openapi/v1.json` | No | OpenAPI spec |

The JSON API lives under `/api/` so the SPA can own the site root; `/health` stays at the root
for Kubernetes probes. Unmatched non-API GETs fall back to `index.html` for SPA client routing.

The `/api` endpoints are generated from the contract (see [Client Generation](#client-generation)):
the app implements one generated `I…Handler` per operation and opts operations out of auth in
`Program.cs`. The `Messages` feature is the worked example — handlers of the generated
`Messages_*` interfaces over an `EntityStore<Message>` (domain `Message` with `Id<T>`), `Page<T>`
pagination, and `IAsyncOnStartup` wiring (a welcome message is seeded on first run).

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

# Kubernetes (from the repo root)
helm install olve-arm src/deploy/helm/
```

## Deployment (GitOps)

This template ships a `.pipelines/` directory, which makes it deploy out of the box via
[**Olve.Pipelines**](https://github.com/OliverVea/Olve.Pipelines) — Oliver's lightweight GitOps CD
service. `.pipelines/config.yaml` is the **single source of truth** for how the app is built and
deployed (the deploy equivalent of `.github/workflows/`): once a pipeline is bound to the repo, the
controller reconciles it to this file and **pushing to `main` redeploys automatically**.

The pipeline shape:

- **Production steps run in parallel** — `build-and-package` (Kaniko build → image tar + Helm chart)
  and `code-test` (the unit suite). A test failure fails the group and **gates the deploy** (nothing
  ships).
- **Processing steps run sequentially** — `deploy-beta` (namespace `apps-beta`) → `deploy`
  (namespace `apps`). **Beta gates prod**: if the beta rollout or its post-deploy health check fails,
  prod never deploys.
- **Secrets are by name only** (`GITHUB_TOKEN`, `SSH_PRIVATE_KEY`); their values live in the
  pipeline's own k8s secret, never in the repo.
- The step scripts source a shared [`olve-lib.sh`](https://github.com/OliverVea/Olve.Pipelines/blob/main/.pipelines/scripts/olve-lib.sh)
  (Kaniko/SSH/Helm footgun helpers) and only parameterize app-specifics, so they stay tiny. They
  fetch it from `main`; swap that for a tag/SHA to pin.

**Homelab conformance.** The Helm chart renders a **`ClusterIP` Service only — no Ingress**
([`Olve.Homelab`](https://github.com/OliverVea/Olve.Homelab) is the edge chart that owns all Ingress).
Routing is registered by adding the app's host + service to the edge chart's `apps:` list in
`values-{beta,prod}.yaml` — **not** in this chart. The `deploy-beta` health-gate probes the
Tailscale-private host `https://<app>-private.ovea.pro/health` from the homelab node, so that host
must be registered in the edge chart before the gate can pass.

### Per-namespace prerequisites (what bites a fresh deploy)

Building and rolling out is automatic, but a generated app needs a few things provisioned in each
target namespace before the pod actually runs. Each of these surfaced on a real deploy:

- **Edge route** — add an entry to `Olve.Homelab`'s `values-{beta,prod}.yaml` `apps:` list (host
  `<app>-private.ovea.pro`, external-dns target `100.100.117.17`, LE TLS). Without it the health
  gate has nothing to probe.
- **OTLP telemetry auth** — beta's `otel-beta.ovea.pro` is unauthenticated (Tailscale); prod's
  `otel.ovea.pro` needs OAuth2 as the shared `otel` client, whose secret is the
  `authentik-oidc-secrets` key **`otel-client-secret`**. (The chart defaults are correct; just
  ensure that secret exists in `apps`.)
- **Authentik CA** — the chiseled image can't validate `*.ovea.pro` TLS, so the chart mounts the
  shared `authentik-ca` configMap (`authentikCa.enabled`). That configMap must exist in the namespace.

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
| `Storage:Mode` | `Ephemeral` | `Ephemeral` (in-memory) or `Persistent` (snapshot to disk) |
| `Storage:Directory` | `data` | Directory for `Persistent` snapshots |

### Persistence

The `Messages` feature is backed by an in-memory `EntityStore<Message>`. By default storage is
`Ephemeral` (state is lost on restart). Set `Storage:Mode=Persistent` to have the store load on
startup and save a debounced whole-snapshot JSON to `Storage:Directory` via the BCL-only
`FileSnapshotStore` — both wired in `Messages/MessageServices.cs`.

Everything sits behind the `ISnapshotStore` seam (`Stores/`), so the persistence ladder — in-memory →
file → S3/MinIO → relational — is a one-line swap at registration without touching the store or
handlers. The `Stores/` module is written at library quality for later promotion to
`Olve.Utilities.Hosting`.

## Client Generation

The contract is `src/spec/main.tsp` (TypeSpec). Everything generated lives in the gitignored
`artifacts/`, never in source folders:

```bash
npm run spec       # src/spec/main.tsp → artifacts/spec/openapi.json + artifacts/generated/backend/*.g.cs
npm run generate   # … then → artifacts/clients/ts (Hey API)
npm run codegen:test   # emitter snapshot tests (UPDATE_SNAPSHOTS=1 to accept changes) + conformance suite (dotnet)
```

The frontend and CLI import the client as `@arm/client` and regenerate it as part of their own
builds. The **backend surface** is generated by our own emitter,
[`src/codegen/typespec-arm-csharp`](src/codegen/typespec-arm-csharp) (plain ESM): records per
model (request shapes split by visibility, e.g. `MessageWritable`), discriminated unions, one
request record + `I…Handler : IHandler<Req, Res>` per operation, `MapArmApi()` with routes,
`.WithValidation` (from `@maxLength` etc.) and declared statuses, a JSON source-gen context and
`ArmApi.HandlerTypes`. Hand-written runtime pieces live in `Olve.AgentRuntimeManager/Api/`:
`ArmResults` maps a handler's `Result` to a declared status (a problem tagged `http:404` → 404
when the operation declares it; else 400 if declared; else the first declared error; else 500),
`ArmBindingFailures` answers binding
failures with the contract's error body, and `UseArmApi()` refuses to start if a handler is
unregistered. The API build compiles the spec itself. The emitter's conformance suite proves the
generated surface matches the spec (see [Build & Test](#build--test)). There is no C# client: the
backend tests speak raw HTTP. See [`docs/SPEC-FIRST.md`](docs/SPEC-FIRST.md).

Run everything the pipeline runs with `npx mise run ci` (mise pins node + dotnet; `npx mise
tasks` lists the tasks).

## Frontend

`src/frontend/` is the template's companion UI: a no-framework, **vanilla Web Components** app in
**TypeScript**, consuming the API through the generated Hey API client. It ships a
`<message-list>` CRUD view over the backend `Message` feature, proving the client-gen →
component → API loop end to end.

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
| **Olve.Utilities** stack (Results, Validation, MinimalApi, Utilities) | Baked-in error handling, validation, result→HTTP mapping, `Id<T>`/`EntityStore<T>` primitives | [docs site](https://olivervea.github.io/Olve.Utilities/) | [OliverVea/Olve.Utilities](https://github.com/OliverVea/Olve.Utilities) | NuGet | *(none yet — gap)* |
| **Olve.Pipelines** | GitOps CD — builds & deploys this repo via `.pipelines/` (see [Deployment](#deployment-gitops)) | in-repo `docs/setup/`, served at `/docs` + `llms.txt` | [OliverVea/Olve.Pipelines](https://github.com/OliverVea/Olve.Pipelines) | [`pipelines-private.ovea.pro`](https://pipelines-private.ovea.pro), beta `pipelines-beta.ovea.pro`, hooks `pipelines-hooks.ovea.pro`; **`pl` CLI** via `GET /download/{asset}` | `ovea-olve-pipelines` |
| **Olve.Homelab** | Edge chart that owns all Ingress; public exposure is registered there, not in this chart | — | [OliverVea/Olve.Homelab](https://github.com/OliverVea/Olve.Homelab) | — | — |
| **TUnit · Rocks · TypeSpec · Hey API · mise** | Tests, AOT mocking, API contract, TS client generation, toolchain + tasks | see per-library links below | — | — | — |

Per-library documentation:

- [Olve.MinimalApi](https://olivervea.github.io/Olve.Utilities/src/Olve.MinimalApi/README.html) — Minimal API extensions for result mapping, validation, and JSON conversion
- [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — Functional result types for non-throwing error handling
- [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — Fluent input validation built on Olve.Results
- [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — Meta-package bundling utility libraries including identifiers, collections, and graph types
- [TUnit](https://tunit.dev/docs/intro) — Test framework (not xUnit/NUnit). Uses `await Assert.That(...)` fluent syntax
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — Source-generated mocking library for AOT-compatible test doubles
- [TypeSpec](https://typespec.io/docs/) — API contract language
- [Hey API](https://heyapi.dev/) — OpenAPI → TypeScript client generator
- [mise](https://mise.jdx.dev/) — toolchain pinning and task runner
