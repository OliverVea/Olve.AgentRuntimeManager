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
- **Nothing hangs the gate.** Every wait in a test is bounded (a `Guard`, `WaitAsync`), a test
  that starts processes kills what it started even when it fails (supervisors detach and would
  outlive it: `TestProcesses.KillSupervisors`), and the run has three nets (`mise.toml`
  `backend:test`): an assembly-level TUnit `[Timeout]` fails a slow test by name; a testhost that
  is silent for 5 minutes is dumped, naming the tests still running (`--hangdump`, which also
  catches a test blocking its thread, which TUnit's timeout doesn't end); the session ends after 15
  minutes (`--timeout`). The gate runs as root in a pod: tests must not assume a login user,
  a reaping pid 1 (an orphan may stay a zombie) or an exec-able `/tmp` beyond what they check.

## Pipeline shape (Olve.Pipelines)

Mirrors QuestionBank. Invoke the `ovea-olve-pipelines` skill before editing `.pipelines/`.

```
production (parallel — a red step blocks ALL processing → no deploy):
  build-and-package     Kaniko → image.tar + src/deploy/vm + version.txt
  test                  fast, no containers: build/typecheck + unit + in-process API tests
                        (WebApplicationFactory on SQLite + FakeProvider)

processing (sequential — beta gates prod):
  deploy-beta           deploy the release into the beta VM (vm-deploy.sh); health gate
  test-after-beta       from its own job, over HTTP only: the HTTP suite against LIVE beta (LLM-free),
                        then the beta checks (one Haiku turn, the rest on `fake`); gates prod
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
  - **CI after-beta** → the beta VM through the host relay, `http://100.100.117.17:18792`

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
- **One exception: the beta checks (2026-10-12; post-deploy, in `test-after-beta`).** After the API
  suite, the same job proves ARM's main paths with real sessions through its API
  (`.pipelines/scripts/beta-check.py`; caller `test-after-beta`), and prod deploys only if every
  check passes. They are post-deploy checks, so they run where the other post-deploy tests run: in
  the pipeline's own job, against beta from outside, over HTTP through the host relay like any
  client. Nothing runs in the VM for testing, and `deploy-beta` only deploys. Both the suite and the
  checks are the bundle's own commit's (its `commit.txt`), so they match the release. The token is a
  client-credentials token of the beta `olve-arm` machine client from beta's Authentik (secret
  `ARM_BETA_OIDC_CLIENT_SECRET`, in the job's environment only; the Authentik CA for that HTTPS is
  read the way the VM gets it, from the cluster's `authentik-ca` configMap through the host).
  **One Haiku turn per bundle:** a `claude` session, created with a repository (the step's
  `BETA_CHECK_REPO_URL`, default this repository over ssh), whose agent runs one small command with
  its Bash tool; the command's own output in the conversation (never the model's wording) proves
  Claude Code started and is logged in, the command ran as the agent user (not `arm`, not root), a
  registered default variable and the session's own reached it, and ARM's config didn't. The same
  command lists each folder in the session's workplace with the top of its git work tree and its
  current branch; the API's answer must list the repository with a `folder` and a `branch` (none is
  a failure), and the check compares the command's output with them: that folder exists, is a work
  tree of its own (not a plain folder, not inside another) and a worktree of a shared copy (its git
  folder apart from the common one), and is on the API's branch. The check
  never works the names out itself. Then the session is deleted (204, then 404), which removes its
  worktree; what is left of it on disk isn't visible through the API, so that part is proved per
  push only (locally, the shared copy lists no worktree afterwards). A run that fails deletes it
  too, and a later run deletes any an earlier one left.
  **Everything else on the `fake` provider** (enabled on beta, never on prod; no model): `/health`,
  server info (the bundle's version), auth (401 without a valid token), provider health (claude and
  fake available), a scripted conversation and its activity checked against each other, a message
  continuing the ended session (M11), a working session refused deletion (409), killed (`killed`,
  by the user) and deleted, and the event stream (the three sessions' lifecycles). About a minute
  in all, every wait bounded (the whole run by the step's `timeout`, 5 minutes). Idempotent per
  bundle: a passing run leaves a record in ARM's history (a `fake` session, "Beta checks passed for
  <version>"), and a retried run for it starts no sessions; a run first kills what a failed one
  left running. Left on beta: the continued fake session and the record (the claude session is
  deleted: ARM keeps its folder and transcript on disk, and its branch in the shared copy).
  **Not checked per deploy:** that nothing of a killed *real* agent runs on (the fake kill proves
  ARM's kill path, not the end of an agent's processes; from outside it would take a second real
  turn), and Claude Code's lockdown (built-in tools only; no MCP servers, plugins or skills), which
  ARM's API doesn't expose: it would need, e.g., the provider's init facts on the session or in its
  conversation. Per push, ClaudeProviderTests kill a stub agent, and the `claude-code` step checks
  the lockdown flags at build time. If a newer release is deployed while the checks run
  (deploy-beta doesn't wait for this step), the record step sees the version change, before or
  after it writes the record (which it then deletes), and fails. The checks themselves are tested per push
  (`BetaCheckTests`, API tests): against this build started locally with a stub Claude Code
  (`BetaCheck/claude-stub.py`) and a local repository; the stub runs the command for real and can
  misbehave (a variable missing, ARM's config leaking, no tool call, a hang, logged out, the wrong
  version, no fake provider, the checkout on another branch, missing, or a plain folder inside
  another work tree), each failure saying what failed and leaving nothing running; test-after-beta's part
  that runs them (the secret in the environment only, each missing prerequisite failing the step
  by name); the check's own logic on what a working ARM never answers (`BetaCheck/beta_check_test.py`).
  New features add their check to `CHECKS` in `beta-check.py` on the `fake` provider where it can
  prove the point (the budget is the one real turn) and a case to those tests.

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
