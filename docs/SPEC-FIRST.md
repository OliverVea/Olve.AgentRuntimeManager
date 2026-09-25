# Spec-first toolchain

How the API contract drives the build, and the milestone details for the toolchain work (M1, M2,
parts of M3, M14). The milestone list itself is [`MILESTONES.md`](MILESTONES.md).
Decision context and spike findings: [`OPEN-QUESTIONS.md` A8](OPEN-QUESTIONS.md#a8-api-definition--code-first-vs-spec-first).

```
src/spec/main.tsp  (the contract; nothing else lives in src/spec/)
  │  tsp compile
  ▼
artifacts/spec/openapi.json  (OpenAPI 3.2, gitignored build artifact)
  ├─► Hey API ─► artifacts/clients/ts  ─► frontend + arm CLI (TS, hand-written on the client)
  ├─► (later) Python / Bash clients, same way
  └─► route-coverage + contract tests ◄─ hand-written backend (minimal API, AOT)
```

**Principles**
- TypeSpec defines the contract; it does not implement the server. The backend stays minimal
  API + Olve.Results (AOT), and tests prove it matches the spec.
- **Generated output never lives in source folders.** OpenAPI documents and clients go to the
  gitignored `artifacts/`.
- **Each project's native build owns its link to `main.tsp`.** `npm run build` / `dotnet test`
  work on their own. **mise** is the single entry point on top: it pins the toolchain and runs
  the same tasks for you, Claude, and CI.
- **Off-the-shelf generators only.** No custom TypeSpec emitter. The CLI is hand-written on the
  generated client.

---

## M1 — One POST, one GET

Scope: the scaffold's `Message` example, spec'd first. `POST /api/messages` and
`GET /api/messages/{id}` (the GET-by-id endpoint is new; the example only has list). The spec
is [`src/spec/main.tsp`](../src/spec/main.tsp).

Build setup (the repo layout this stage establishes):

```
mise.toml            tools: node, dotnet, bun (exact versions) + tasks: spec, build, test, ci
package.json         npm workspaces (src/frontend, src/cli) + TypeSpec tooling (@typespec/*) + Hey API
tspconfig.yaml       emitter config: openapi-versions ["3.2.0"], file-type json, output-dir artifacts/spec
src/spec/main.tsp
src/backend/         dotnet; contract tests compile the spec before running (MSBuild target)
src/frontend/        npm run build = tsp compile → Hey API → vite
src/cli/             npm run build = tsp compile → Hey API → bun build --compile (single binary)
artifacts/           everything generated (gitignored)
```

- [x] `src/spec/main.tsp`
- [ ] `mise.toml` pinning node/dotnet/bun; tasks `spec`, `build`, `test`, `ci` delegating to native commands; `mise run ci` is exactly what the pipeline runs
- [ ] Root `package.json` (workspaces, pinned `@typespec/*` 1.16, Hey API) + `tspconfig.yaml`
- [ ] **Hey API replaces Kiota.** Frontend imports the generated client from `artifacts/`; the committed `src/frontend/src/api/` and the Kiota tool/runtime pin go away
- [ ] Spec covers every `Message` operation the frontend uses (list/create/update/delete), or the frontend drops what the spec doesn't define
- [ ] Server endpoints hand-written against the spec
- [ ] **Route-coverage test**: every spec operation (method + path) maps to an endpoint in `EndpointDataSource`, and every `/api` endpoint is in the spec
- [ ] **Contract test**: responses validate against the emitted schemas
- [ ] Minimal `src/cli`: `arm message create/get` with `--pretty` (default) / `--json`, compiled to one binary with `bun build --compile`
- [ ] Pipeline steps install mise and run its tasks (invoke `ovea-olve-pipelines` before touching `.pipelines/`); Dockerfile Node stage runs the frontend build (tsp + Hey API + vite); the .NET publish stage stays Node-free

Exit: changing `main.tsp` without the server (or vice versa) turns `mise run ci` red, locally
and in the pipeline.

## M2 — SSE

Scope: one typed event stream with filters and replay, modelled the way SPEC §Event Bus
needs it.

```tsp
import "@typespec/streams";
import "@typespec/events";
import "@typespec/sse";
using Events; using SSE;

model EventFilter {
  @query(#{ explode: false }) event?: string[];
  @query(#{ name: "exclude_event", explode: false }) excludeEvent?: string[];
  @header("Last-Event-ID") lastEventId?: string;
}

@events
union ArmEvent {
  @Events.contentType("application/json") heartbeat: { type: "heartbeat", timestamp: utcDateTime },
  @Events.contentType("application/json") `message.created`: { type: "message.created", message: Message },
}

@route("/events") @get op events(...EventFilter): SSEStream<ArmEvent>;
```

- [ ] Server: SSE endpoint (`TypedResults.ServerSentEvents`), server-enforced include/exclude filter, `Last-Event-ID` replay from an in-memory buffer
- [ ] **Every event payload carries a `type` discriminator.** Hey API ignores `itemSchema`, so the discriminated payload union is what gives clients per-event types
- [ ] Clients use Hey API's SSE stream (`for await`, built-in reconnect + `Last-Event-ID` + backoff)
- [ ] Contract test: each emitted event's `data` validates against its `event`'s schema
- [ ] `arm events [--event X] [--exclude-event X]` tails with `--pretty` / `--json` (NDJSON)

Exit: a filter in the spec is enforced by the server and exposed by the CLI.

## M3 (part) — `QUERY` + `queryMethod`

SPEC wants one search operation that can be served three ways (`query` | `get` | `post`).
TypeSpec has no `QUERY` verb decorator.

- [ ] Choose the modelling: custom decorator that rewrites the operation into an OpenAPI 3.2
  `query` op, vs. `@post /…/search` in the spec with `QUERY` + `GET` routed by the server as
  aliases
- [ ] Server maps the configured method via `MapMethods` (A1/B4), or natively on .NET 11; route-coverage test understands the alias
- [ ] Check Hey API behaviour with a `query` operation before committing to it
- [ ] `arm session list` with the search body as flags

## M3 (part) — Errors & multi-status responses

- [ ] SPEC error envelope as shared `@error` models; A2 mapping layer in Olve.Results → HTTP
- [ ] `201 | 202 {queuePosition} | 409 | 503` style unions round-trip through the client and CLI exit codes
- [ ] `Idempotency-Key` header modelled once as a reusable parameter

## CLI (packaging in M1; each milestone adds its commands)

- [ ] Command structure matching the SPEC command tables (`arm <entity> <verb>`), hand-written on the generated client
- [ ] Output modes: `--pretty` (default: tables for lists, key/value for objects), `--json`, NDJSON for streams; `--binary` if the protobuf path ever lands
- [ ] Exit codes mapped from error envelope / status
- [ ] Release artifact: `bun build --compile` per target (linux-x64/arm64, darwin-arm64), downloadable like `pl`
- [ ] Optional: Python / Bash clients from the same OpenAPI artifact, if a consumer wants them

## M14 — Versioning

- [ ] B3 via TypeSpec `@versioned` (prefix vs header); breaking-change check between versions in CI

## Template write-up (after M3)

- [ ] What to lift into the `olve-api` template (spec → artifacts → clients, mise, contract tests) and what stays ARM-specific

---

## Known constraints (from the spikes)

- Emit **OpenAPI 3.2**. 3.1 drops SSE `itemSchema`.
- `@typespec/http-server-csharp` output is unusable (MVC, non-AOT, lossy, doesn't compile). Don't generate the server from it.
- `@typespec/http-client-js` (preview) is not usable yet: wrong query key for `exclude_event`, SSE returned as `Promise<string>`, read-only fields sent on create. Re-check later.
- Hey API 0.99 reads 3.2, honours read-only (`MessageWritable`), serializes query params correctly, and has real SSE streaming; it types the stream as `unknown` (ignores `itemSchema`), hence the `type` discriminator. It crashed with the newest TypeScript and worked on TypeScript 5, so pin TypeScript.
- A `bun build --compile` CLI on the Hey API client is ~81 MB and depends only on libc (a .NET AOT binary would be ~10–15 MB).
- .NET 11 (Nov 2026) emits OpenAPI 3.2 with SSE `itemSchema` from `TypedResults.ServerSentEvents` and supports `QUERY`: useful for a server-side conformance comparison.
