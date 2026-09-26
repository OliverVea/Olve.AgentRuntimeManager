# Standards

Rules for code and behaviour in this repo. MUST = required; SHOULD = default unless there's a
reason. Back a rule with a test where possible.

## API behaviour

- `GET` and searches (`POST /api/<resource>/search`) MUST NOT have side effects beyond logging,
  metrics and tracing.
- `PUT`, `DELETE` and searches MUST be idempotent; creating `POST`s SHOULD accept `Idempotency-Key`.
- Every error MUST use the `{ "error": { code, message, details } }` envelope; codes are
  `SCREAMING_SNAKE_CASE` and stable once published.
- Every status an operation can answer MUST be declared in the contract. Handlers return the
  generated per-operation response union (`SessionsKillResponse.Conflict(…)`), so an undeclared
  status doesn't compile; expected failures are declared responses, never exceptions.
- The contract (`src/spec/`, entry point `main.tsp`) is the source of truth: every operation MUST be implemented,
  and every `/api` endpoint MUST be in the contract. The generated surface guarantees this (and
  the declared statuses and schemas); the emitter's conformance suite tests it once for all services.
- Timestamps MUST be UTC ISO-8601; IDs are opaque strings.
- Secrets MUST NOT appear in responses, logs, events or traces.
- `/health` is anonymous and says only up/down; diagnostics live behind auth (`GET /api/health`).
  Whether `/health` is reachable publicly is decided in networking (edge/firewall), not in code.

## Events

- Every event MUST carry `type`, `at` and its subject ID.
- Names are dotted namespaces in past tense or state form (`session.approval.resolved`,
  `session.waiting`); one event per fact, no duplicates.
- Payloads SHOULD be bounded; oversized fields are truncated with `truncated: true`.

## Clients

- The `arm` CLI MUST cover every API feature: nothing supported should need a raw HTTP call. A
  backend change MAY ship first (deployed and verified on its own), but the feature isn't done
  until the CLI has it.
- The web UI SHOULD offer every feature too, and MAY simplify it (fewer options, friendlier
  defaults) where the CLI is exhaustive. Client changes land together: no client-only features.

## Code

- Generated output MUST go to `artifacts/`, never into source folders.
- Expected failures use Olve.Results; exceptions are for bugs.
- The backend runs on the JIT, published self-contained; Native AOT is not a goal. Source-generated
  JSON is still preferred where it's cheap.
- Package versions are central (`Directory.Packages.props`); no `Version` attributes in csproj.
- Tests use TUnit; API tests and the emitter's conformance suite speak raw HTTP.
