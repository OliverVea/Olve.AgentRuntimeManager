# Standards

Rules for code and behaviour in this repo. MUST = required; SHOULD = default unless there's a
reason. Back a rule with a test where possible.

## API behaviour

- `GET` and `QUERY` MUST NOT have side effects beyond logging, metrics and tracing.
- `PUT`, `DELETE` and `QUERY` MUST be idempotent; creating `POST`s SHOULD accept `Idempotency-Key`.
- Every error MUST use the `{ "error": { code, message, details } }` envelope; codes are
  `SCREAMING_SNAKE_CASE` and stable once published.
- The contract (`src/spec/main.tsp`) is the source of truth: every operation MUST be implemented,
  and every `/api` endpoint MUST be in the contract.
- Timestamps MUST be UTC ISO-8601; IDs are opaque strings.
- Secrets MUST NOT appear in responses, logs, events or traces.
- `/health` is anonymous and says only up/down; diagnostics live behind auth (`GET /api/health`).
  Whether `/health` is reachable publicly is decided in networking (edge/firewall), not in code.

## Events

- Every event MUST carry `type`, `at` and its subject ID.
- Names are dotted namespaces in past tense or state form (`session.approval.resolved`,
  `session.waiting`); one event per fact, no duplicates.
- Payloads SHOULD be bounded; oversized fields are truncated with `truncated: true`.

## Code

- Generated output MUST go to `artifacts/`, never into source folders.
- Expected failures use Olve.Results; exceptions are for bugs.
- The backend runs on the JIT, published self-contained; Native AOT is not a goal. Source-generated
  JSON is still preferred where it's cheap.
- Package versions are central (`Directory.Packages.props`); no `Version` attributes in csproj.
- Tests use TUnit; contract/integration tests speak raw HTTP.
