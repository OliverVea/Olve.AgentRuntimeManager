# ARM — Testing Strategy

How ARM is tested across the pipeline and how the provider (LLM) integration is exercised
without cost or flakiness. Modelled on QuestionBank's proven `.pipelines/` shape (hermetic
gate pre-deploy, real suite against live beta). See [`OPEN-QUESTIONS.md`](OPEN-QUESTIONS.md)
(A5 persistence, A3 auth) and [`SPEC.md`](SPEC.md).

## Principles

- **Cost-safe by default.** ARM's job is spawning LLM agents; an unguarded test run spends
  real tokens. Every per-push test path is **LLM-free** (FakeProvider or mock endpoint).
  Real-API calls live only in a small, gated, off-hot-path suite.
- **Deterministic in the gate.** No network, no real LLM in the production gate.
- **Test each engine where it lives.** SQLite in-process pre-deploy; Postgres live via beta.
- **Test the real artifact once.** The image built in the pipeline is validated against live
  beta — no separate image-build-and-test step pre-deploy.

## Pipeline shape (Olve.Pipelines)

Mirrors QuestionBank. Invoke the `ovea-olve-pipelines` skill before editing `.pipelines/`.

```
production (parallel — a red step blocks ALL processing → no deploy):
  build-and-package     Kaniko → image.tar + src/deploy/vm + version.txt
  test                  fast, no containers: build/typecheck + unit + in-process API tests
                        (WebApplicationFactory on SQLite + FakeProvider)

processing (sequential — beta gates prod):
  deploy-beta           deploy ARM + its private Postgres to apps-beta; rollout + health gate
  test-after-beta       the HTTP suite against the LIVE beta Service; LLM-free; gates prod
  deploy                prod
```

**No image-building Testcontainers in the gate.** (Decision — the "real image over HTTP"
role is served once by `test-after-beta`, which tests the very artifact being promoted.)
Building an image in a production step to test it, then deploying and testing it again, is
doing the expensive thing twice.

### Persistence engines across the pipeline

- **SQLite** — embedded, so it runs **in-process in the production gate** for free (no container).
- **Postgres** — the beta/prod engine, validated **live by `test-after-beta`** (beta gating
  prod is exactly the canary meant to catch a PG dialect bug).
- **Optional later:** if earlier PG feedback is wanted, add a Testcontainers-PG **DB-only**
  lane (spin just the database, app in-process — far lighter than image-building). Not now.

### One HTTP suite, two targets

The API tests (`src/backend/Olve.AgentRuntimeManager.ApiTests`, target chosen by `ApiTarget`)
are one suite of behaviour tests over raw HTTP (implemented 2026-09-26):
- **default: in-process** (`WebApplicationFactory`) → the `test` gate (`dotnet test`, `mise run ci`)
- **base-URL mode** (`ARM_API_BASE_URL`) → any running server:
  - **local dev** → `http://localhost:<port>` (a `dotnet run` instance)
  - **the real image** → `mise run api:image` builds the `Dockerfile` image, starts it with a test
    signing key and runs the suite against it (manual; needs Docker)
  - **CI after-beta** → the in-cluster beta Service `http://olve-arm.apps-beta.svc.cluster.local`

In base-URL mode the suite mints tokens only if given the target's auth settings
(`ARM_API_SIGNING_KEY`, `ARM_API_ISSUER`, `ARM_API_AUDIENCE`); otherwise tests that need one are
skipped with that reason. Tests that inspect the host (DI, configuration) run in-process only.
The scaffold's Testcontainers `AppFixture` / IntegrationTests project is gone. No Docker in CI.

Contract conformance (every operation routed, responses valid for their declared statuses) is
**not** re-tested per service: it's the generator's responsibility, covered once by the
emitter's conformance suite (`src/codegen/typespec-arm-csharp/test/conformance`, run by
`mise run codegen:test`; see [`SPEC-FIRST.md`](SPEC-FIRST.md) M2).

### `test-after-beta` specifics (from QuestionBank)

- Hit the **in-cluster Service** directly (no ingress dependency); readiness-gate on `/health`.
- **Self-authenticate** — the suite mints its own token (this is the **A3** dependency:
  a local-HMAC test token or an OIDC machine client via pipeline secrets).
- **Isolated data** — use a dedicated caller/tenant or ephemeral sessions so it never
  clobbers real beta state.
- **LLM-free** — control-plane only (session CRUD, approval flow via FakeProvider, policy,
  SSE, health). Never spawn a real Claude/Codex session here.
- **One exception, in `deploy-beta` (2026-09-26):** each bundle's beta deploy runs one real
  Claude Code turn in the VM (`src/deploy/vm/claude-check.sh`: Haiku, "Tell me a joke", the
  provider's lockdown, the beta token) and fails unless the agent saw no tools or MCP servers and
  the turn succeeded. It confirms the bundle's Claude Code version, login and lockdown before prod.

## Provider (LLM) integration testing

The layers, most-isolated/cheapest first. ARM's provider + `IAgentExecutor` seams make each a
registration/config swap, not special test hooks — ARM already owns the injection points
(`buildEnv` for Claude, the per-session TOML in `CODEX_HOME` for Codex).

- **L1 — FakeProvider (boundary mock).** A provider that emits scripted `stream-json` events;
  no CLI, no network. Covers all orchestration: session lifecycle, approval flow, SSE,
  parsing→event-model. The bulk of tests, per-push. *Weakness:* tests your model of the CLI's
  output, which drifts when the CLI changes.
- **L2 — Recorded CLI transcripts (parser fidelity).** Capture real
  `claude … --output-format stream-json` / `codex … --json` output once, save as golden
  fixtures, replay into ARM's parser (or via a stub binary on `PATH`). Catches parser
  regressions against *real* output; deterministic and free. Re-record on CLI bumps.
- **L3 — Real CLI → mock LLM endpoint (full integration, no cost).** Run the actual CLI but
  point its API base URL at a local fake that speaks the Anthropic/OpenAI streaming (SSE)
  format. Highest-fidelity cheap test: real NDJSON, real resume args, **real MCP handshake**.
  - Redirect via the **base-URL knob, not TLS-MITM**: Claude `ANTHROPIC_BASE_URL`; Codex a
    custom `base_url` under `model_providers` in its per-session config. *(Verify exact knobs
    for current CLI versions — see open item below.)*
  - **MCP is not mocked** — ARM routes all tools through its own MCP server, so tool calls hit
    the **real** MCP server; only the LLM *completions* HTTPS is mocked. Real CLI + real MCP +
    mock-completions ≈ full integration at zero token cost.
  - MITM (mitmproxy + CA via `NODE_EXTRA_CA_CERTS`/`SSL_CERT_FILE`) only if a CLI ignores the
    base-URL knob — brittle, avoid.
  - Seed the mock from L2 recordings (replay real bytes) rather than hand-writing the SSE
    format.
- **L4 — Gated live drift suite.** A handful of tests hitting the **real** API — cheapest
  model, cost-capped, **nightly/manual, never in the per-push gate**. The only thing that
  catches genuine CLI/API drift.

**Usual split:** L1 + L2 per-push in CI; L3 when validating real-CLI behaviour (resume, MCP);
L4 nightly.

## Components ARM must build for this

- **`FakeProvider`** — deterministic scripted provider (L1; also used by `test-after-beta`).
- **Record/replay LLM stub** — a mock endpoint speaking the provider SSE format, seeded from
  recorded transcripts (L2/L3).
- **`ApiTarget` base-URL mode** — the one HTTP suite pointed at localhost, the image or the beta
  Service (done; `test-after-beta` still needs its auth, see Open).

## Open

- **Exact base-URL knobs** for the current Claude Code and Codex CLIs (confirm via context7
  docs before implementing L3).
- **`test-after-beta` auth** — resolved by A3 (local-HMAC test token vs OIDC machine client).
