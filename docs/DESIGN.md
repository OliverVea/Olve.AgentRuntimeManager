# Design: Completing the Olve.AgentRuntimeManager ecosystem

Status: draft / proposal. Covers the four gaps surfaced when comparing the template
against `Olve.Pipelines` and `QuestionBank`: **backend primitives**, a **frontend
template**, **GitOps (`.pipelines/`)**, and **documentation/references**.

## Goals & principles

- **Opinionated by design — about practices and own tooling.** This is a personal
  template: it deliberately bakes in the Olve.* stack (Results, Validation, MinimalApi,
  Utilities, the promoted `EntityStore`) and Oliver's best-practices defaults (minimal
  API + `Configuration/` pattern, Result-based error handling, JWT/OIDC, OTLP telemetry,
  AOT). Those opinions are the point; a new service inherits them on purpose.
- **Opt-in — about framework/runtime lock-in, especially on the frontend.** The opt-in
  rule is narrow: do **not** force heavyweight third-party framework machinery or
  always-on runtime behavior that a consumer can't escape — e.g. a FE framework
  (React/Vue/Svelte) or automatic re-rendering. Those are reach-for-it choices, never
  inherited baggage. The distinction: baked-in = *Oliver's own lightweight primitives
  and practices*; opt-in = *framework lock-in / machinery you didn't ask for*.
- **Third-party dependencies stay in a corner; own packages don't have to.** A
  third-party dependency we take should be **narrowly scoped, ideally have zero
  transitive dependencies, be performant and flexible, and be replaceable** — confined
  to a "corner" of the codebase behind a seam, not threaded throughout. If it's
  pervasive it's hard to swap, which defeats replaceability. **Oliver's own packages
  (Olve.*) are explicitly exempt** — they may be used throughout; that's the whole
  point of baking them in (see the opinionated/opt-in split above). This is *why* the
  Olve.* stack is everywhere but a thing like Lit, if ever used, lives only inside the
  components that opt into it.
- **No drift from the proven sibling.** `Olve.Pipelines` is effectively "this template
  after a year of real use." Where it has solved a problem well, lift the solution
  into a shared home (Olve.Utilities, a shared script lib) rather than copy-pasting.
- **Demonstrate the stack with one real feature.** A single end-to-end CRUD example
  (`Message`) should exercise the primitives so the template documents itself.

---

## 1. Backend (BE)

### 1.1 Promote `EntityStore<T>` + `Event<T>` into Olve.Utilities

Today `EntityStore<T>` lives in `Olve.Pipelines/src/Olve.Pipelines/Shared/`. It depends
only on:

- `Id<T>`, `IHasId<>` — **already in Olve.Utilities** (`Lookup` namespace)
- `DeletionResult` — **already in Olve.Results**
- `Event<T>` — a ~10-line `Action`-based pub/sub, currently app-local

It is the **mutable, observable sibling** of the existing `IdFrozenLookup<T, TId>`. It
belongs next to it.

**Move into Olve.Utilities** (new namespace, e.g. `Olve.Utilities.Stores`):

- `Event<T>` (`Invoke`/`Subscribe`/`Unsubscribe`)
- `EntityStore<T>` (`Set`/`Mutate`/`TryGet`/`List`/`Delete`/`Contains` + `OnAdded`/`OnUpdated`/`OnDeleted`)
- `EntityStoreIndex<T, TKey>` and `EntityStoreUniqueIndex<T, TKey>`

> **Coordination note:** Oliver is editing `EntityStore` right now. Promotion should
> happen *after* that settles; promote the final shape, don't fork it. Pipelines then
> deletes its local copy and consumes the package version.

**Open question:** namespace name (`Olve.Utilities.Stores` vs `Olve.Utilities.Lookup`
alongside `IdFrozenLookup`). Recommendation: `Stores`, since these are mutable/eventful,
unlike the frozen lookups.

#### 1.1.1 `Event<T>` — keep the shape, harden as follow-up

The current `Event<T>` is a synchronous multicast delegate (`Action<T>`): `Invoke`
runs every subscriber inline, on the calling thread, in registration order, and the
caller blocks until they finish.

```csharp
public class Event<T>
{
    private Action<T>? _handlers;
    public void Invoke(T message) => _handlers?.Invoke(message);
    public void Subscribe(Action<T> handler) => _handlers += handler;
    public void Unsubscribe(Action<T> handler) => _handlers -= handler;
}
```

**Decision: this is the right shape. Lock the public surface now; harden internals as a
follow-up.** As long as the consumer-facing shape (`Subscribe`/`Unsubscribe`, and the
producer-facing `Invoke`) stays stable, the robustness fixes below are non-breaking and
need not block promotion or the `Message` rebuild.

**Why synchronous, not channels (recorded rationale).** `Event<T>` and
`System.Threading.Channels` solve *different* problems and should not be conflated:

- `Event<T>` is **synchronous in-process notification** (push; caller blocks until
  handlers run). `Channels` is an **async producer/consumer queue with backpressure**
  (buffered handoff; consumer pulls).
- For this workload `Event<T>` is the *cheaper* option: `Invoke` allocates nothing on
  the hot path (no boxing for a value-type `Id<T>`); allocation happens only at
  `Subscribe` time (startup). A channel would allocate/enqueue per message and add a
  background consumer + async state machine to deliver a notification you want handled
  now — a performance regression, not an optimization.
- **Synchronous dispatch is a correctness requirement, not just a preference.**
  `EntityStoreIndex` subscribes to `OnAdded`/`OnDeleted` to maintain its index; that
  update must happen synchronously with the mutation, or a reader could observe a `Set`
  before the index reflects it. Putting a channel between the store and its own indexes
  turns a consistency guarantee into a race.
- Channels belong **in a specific subscriber that genuinely needs async decoupling**,
  not in the shared primitive. Pipelines already does this: `OnUpdated.Subscribe(_ =>
  RequestSave())` returns instantly and debounces the real S3 write (a coalescing
  queue-of-one). Same principle as the bus discussion — bridge to heavier machinery at
  the subscription site, keep the primitive sync and cheap.

**So the gaps are correctness, not performance.** Follow-up hardening (shape-preserving):

1. **Thread-safe subscribe/unsubscribe.** `_handlers += h` is a non-atomic
   read-modify-write; `EntityStore` is concurrent (`ConcurrentDictionary` + CAS
   `Mutate`) and can `Invoke` while another thread subscribes, silently dropping a
   subscription. Fix with `Interlocked.CompareExchange` on the delegate field (or an
   immutable handler array). *(The C# `event` keyword generates exactly this; the plain
   field does not.)*
2. **Per-handler exception isolation.** A throwing subscriber currently propagates out
   of `Set`/`Delete`/`Mutate` and skips the remaining handlers — and since
   `EntityStoreIndex` is itself a subscriber, one buggy app handler can abort a write
   *and* desync an index. Catch per handler; don't let one subscriber break the
   mutation or the others.
3. **`IDisposable` subscriptions** (shape-additive). Return a disposable from
   `Subscribe` for leak-free unsubscription of lambdas. Matters for transient
   subscribers; startup singletons (lifetime = app) are unaffected.

**Producer/consumer split (the interface seam worth having).** Expose a subscribe-only
interface and keep `Invoke` on the concrete type the store owns, so subscribers cannot
fire events:

```csharp
public interface IReadEvent<out T> { IDisposable Subscribe(Action<T> handler); }
public sealed class Event<T> : IReadEvent<T> { public void Invoke(T message) { /* ... */ } /* ... */ }
```

`EntityStore` then exposes `IReadEvent<Id<T>> OnAdded { get; }` etc. **Not doing:**
injecting a pluggable event bus into `EntityStore`. If an app wants a real bus
(MediatR / Channels / outbox), it subscribes one forwarding handler that republishes —
full freedom without complicating the store's common path.

**Footguns to document:** `async void` handlers become unobserved fire-and-forget under
sync dispatch (steer away; exception isolation partly mitigates); and the delegate keeps
subscribers alive (the leak case `IDisposable` addresses).

#### 1.1.2 Persistence: should Olve.Utilities own it? — Yes, the orchestration + port

**Decision: yes.** Promote the persistence *orchestration* and the storage *port* into
Olve.Utilities; keep every backing-store *adapter* in the app. This is "lift the proven
solution into a shared home" — Pipelines already has the pattern, duplicated four times
(`JobPersistenceService`, `ConfigurationPersistenceService`, `PromotionPersistenceService`,
`BindingHookPersistenceService` in `Olve.Pipelines/.../Shared/Persistence/`), each
re-implementing the same ~140-line lifecycle. That duplication is the signal.

**Why split orchestration from backing store (dependency principle).** The backing store
is what pulls dependencies (`Amazon.S3`, EF Core, …) — those must **not** enter
Olve.Utilities. So Utilities owns:

1. **The storage port** — promote/generalize Pipelines' `ISnapshotStore` (a blob seam,
   key → `byte[]`, `TryReadAsync` returns `null` when absent so callers never overwrite
   good state with empty). Adapters (`S3SnapshotStore`, a file one for local/template
   default, SQLite, …) live in the app and carry their own deps — behind the seam,
   replaceable.
2. **The orchestrator** — a generic `EntityStorePersister<T>` (lives in
   `Olve.Utilities.Stores` next to `EntityStore<T>`) that encapsulates the whole
   lifecycle the four Pipelines services hand-roll:
   - **load on startup → populate** the store (under a `_loading` guard so repopulation
     doesn't echo back as saves),
   - **subscribe** to `OnAdded`/`OnUpdated`/`OnDeleted` → `RequestSave()`,
   - **debounced/coalesced save** (a `Timer`/`TimeProvider` tick flushes a `_dirty` flag,
     so N mutations = at most one write per interval — the "coalescing queue-of-one" from
     §1.1.1),
   - **flush on shutdown**,
   - and the subtle **safety policy** that's the real reason to libraryize it once:
     - Ephemeral mode → no load/save, ready immediately;
     - Persistent mode requires a store, else fail fast;
     - load failure → **throw** (crashloop) rather than overwrite good state with empty;
     - corrupt snapshot → log critical + throw (terminal, manual restore);
     - first run (`null` read) → writing an empty baseline is safe;
     - never save before a load is confirmed.

**Keeping it AOT-clean and dependency-free.** Utilities must not call reflection-based
`JsonSerializer`. So the persister takes **serialize/deserialize delegates**, letting the
app supply its source-gen `JsonContext`:

```csharp
services.AddEntityStorePersistence<Message>(
    key: "messages.json",
    serialize:   msgs  => JsonSerializer.SerializeToUtf8Bytes(msgs, AppJsonContext.Default.IReadOnlyListMessage),
    deserialize: bytes => JsonSerializer.Deserialize(bytes,        AppJsonContext.Default.IReadOnlyListMessage));
// app separately registers an ISnapshotStore impl: file for local/template default, S3 in prod.
```

Dependencies needed: only BCL (`Timer`/`TimeProvider`, `Task`) + `DI.Abstractions`
(already referenced) + the existing `IAsyncOnStartup`/hosted-lifecycle hook for
load/flush. **No new heavy deps.**

**Scope guards.**
- **v1 = whole-snapshot, single store.** Per-entity/delta saves are a later optimization
  (the events already carry `Id<T>`, so it's feasible) — note it, don't build it yet.
- **Multi-store-into-one-snapshot** (Pipelines persists `jobs` + `jobGroups` together) →
  leave to app composition; don't over-generalize the generic to N stores.
- **Opt-in.** `EntityStore<T>` works standalone with its events public; the persister is
  an *added layer*, never required — matches the opt-in principle.

**Packaging (resolved) — split along the *dependency* boundary, not the domain.** The
deciding fact: of the pieces, only the orchestrator actually needs a host. The port is a
zero-dep interface and a file adapter is BCL-only, so both stay in core next to the store
they serve; only `EntityStorePersister<T>` pulls `Microsoft.Extensions.Hosting.Abstractions`
(for `IHostedLifecycleService`'s load/flush), so it — and only it — moves to a new package.
This keeps core primitives-only (its dep set is unchanged but for BCL `System.IO`) and keeps
adapter names sane (an S3 adapter is a *store*, not a *hosting* concern).

```
Olve.Utilities (core)              deps: unchanged (+ BCL System.IO only)
└─ Stores/
   ├─ EntityStore<T>, indexes, Event   (already promoted)
   ├─ ISnapshotStore                ← the port, zero-dep
   └─ FileSnapshotStore             ← BCL-only default adapter (local + template default)

Olve.Utilities.Hosting (NEW)       deps: core + Microsoft.Extensions.Hosting.Abstractions
├─ EntityStorePersister<T> : IHostedLifecycleService, IDisposable
├─ AddEntityStorePersistence<T>(...)   DI extension
├─ StorageMode (Ephemeral | Persistent)
└─ (future tenants: a hosted IAsyncOnStartup runner; the IPersistenceReadiness gate —
    both are host-lifecycle concerns currently duplicated in Pipelines)

Olve.Utilities.Stores.S3 (future)  deps: core + Amazon.S3
└─ S3SnapshotStore : ISnapshotStore
```

`Olve.Utilities.Hosting` becomes the single home for everything in Olve.Utilities that
legitimately takes a host-lifecycle dependency — keeping core free of it permanently.

**Staging (resolved) — incubate in the template first, promote later.** The package layout
above is the *target*, not the first step. v1 ships **inside `Olve.AgentRuntimeManager`** as a
self-contained, promotion-shaped module (proposed `src/Olve.AgentRuntimeManager/Stores/`, sitting
alongside the existing `Configuration/`, `Health/`, `Message/` modules). Reasons:

- A template must be **self-contained and installable** — taking a dependency on an
  unreleased `Olve.Utilities.Hosting` would break `dotnet new` until that package ships and
  versions align. In-app keeps the template runnable today.
- **Incubate-then-promote is the proven path** — this pattern was born as app code in
  Pipelines; letting it prove itself against a real consumer (the §1.2 `Message` feature)
  before freezing a public package surface is the right sequencing.
- A template is the **seed of every generated app**, so the in-app copy is written at
  **library quality now** — exact v1 surface below, full acceptance criteria, its own
  folder/namespace — so promotion to `Olve.Utilities.Hosting` is a near-mechanical
  copy-folder / swap-namespace / add-csproj move, not a rewrite.

**Hard prerequisite (ordering constraint).** The in-app persister builds on `EntityStore<T>`,
which lives in `Olve.Utilities.Stores` — and that code is **not merged, let alone released**.
It exists only on a **local-only, unpushed** Olve.Utilities branch `feat/promote-entity-store`
(commit `366e71f`, branched before v0.44.0) — not on `master`, not on any remote branch, and
not in v0.44.0 or v0.45.0 (current release). The template references
**Olve.Utilities 0.41.0**, so it has no `EntityStore` today. Therefore, before the in-app
persister can compile:

1. **Merge `feat/promote-entity-store` to master** in Olve.Utilities — rebasing over v0.44.0's
   `Page<T>` breaking change *and* over v0.45.0's new `MustBeUsedWhenReturned` analyzer (the
   promoted `EntityStore.Mutate`/`Delete` return `Result`/`DeletionResult`, so the analyzer
   will require those to be observed at every call site, including inside the new code).
2. **Cut an Olve.Utilities release** (≥ v0.46.0) including that merge.
3. **Bump the template's Olve.\* references** (`Directory.Packages.props`) from 0.41.0 to that
   release. This pulls in `EntityStore` *and* the `MustBeUsedWhenReturned` analyzer across the
   whole template at once — expect the analyzer to surface unobserved-`Result` diagnostics in
   existing template code too; fix or suppress those in the same bump.

(Vendoring `EntityStore` into the template instead is explicitly rejected — it would fork the
type the §1.1 promotion just unified.)

**Two corrections to the framing above, from reading the actual prior art.**

1. *It's three snapshot services, not four, and none is single-store.* The real
   snapshot-lifecycle services are `JobPersistenceService` (2 `EntityStore`s),
   `ConfigurationPersistenceService` (6+ stores, mixing `EntityStore` **and**
   `AttachmentStore`), and `PromotionPersistenceService` (1 `AttachmentStore`). There is no
   `BindingHookPersistenceService` — bindings ride inside `ConfigurationSnapshot`.
   `BundlePersistenceService` exists but is a *different* pattern (per-artifact `IBundleStore`,
   no debounce, no readiness gate). Consequence: a v1 single-`EntityStore<T>` persister
   **will not directly delete any of the three** — they are all multi-store or
   `AttachmentStore`-based. v1's value is (a) the distilled safety lifecycle as a reusable
   unit and (b) the single-store building block, whose **first real consumer is the §1.2
   `Message` CRUD feature**. Pipelines dedup is a later step via app-level composition over
   the same orchestrator (and a possible `AttachmentStore` persister sibling).

2. *Promotion is not zero-new-deps.* Core Olve.Utilities references neither
   `Hosting.Abstractions` nor `IHostedLifecycleService` — only its own `IAsyncOnStartup`,
   which covers load-on-startup but **not** flush-on-shutdown. The proven lifecycle needs
   `StartingAsync`/`StartedAsync`/`StoppingAsync`, hence the `Olve.Utilities.Hosting` package
   above carrying the one new (AOT/trim-clean, low-transitive) dependency.

**Concrete v1 surface.**

```csharp
// core — Olve.Utilities.Stores
public interface ISnapshotStore
{
    Task<byte[]?> TryReadAsync(string key, CancellationToken ct);   // null == absent (first run)
    Task WriteAsync(string key, byte[] content, CancellationToken ct);
}

public sealed class FileSnapshotStore : ISnapshotStore { /* BCL System.IO; atomic write via temp+move */ }

// Olve.Utilities.Hosting
public enum StorageMode { Ephemeral, Persistent }

public sealed class EntityStorePersister<T> : IHostedLifecycleService, IDisposable
    where T : IHasId<Id<T>>;   // ctor(EntityStore<T>, options, TimeProvider, ILogger<…>, ISnapshotStore? = null)

public static IServiceCollection AddEntityStorePersistence<T>(
    this IServiceCollection services,
    string key,                                       // e.g. "messages.json"
    Func<IReadOnlyList<T>, byte[]> serialize,         // app supplies its source-gen JsonContext
    Func<byte[], IReadOnlyList<T>?> deserialize,
    StorageMode mode = StorageMode.Persistent,
    TimeSpan? saveInterval = null)                    // default 1s; coalescing queue-of-one
    where T : IHasId<Id<T>>;
```

`TimeProvider` (not a raw `Timer`) drives the debounce so the interval is testable.
Serialize/deserialize are delegates — Utilities never calls reflection-based
`JsonSerializer`, preserving AOT-cleanliness (§1.1.2 gotcha).

**Acceptance criteria — the safety policy, verbatim from the proven services.** Each is a
test against a faked `ISnapshotStore`:

- [ ] **Ephemeral** mode → no load, no save, ready immediately; `ISnapshotStore` may be absent.
- [ ] **Persistent** mode with no `ISnapshotStore` registered → fail fast at startup (throw).
- [ ] **Load failure** (`TryReadAsync` throws — transient/auth) → **throw** from startup; never
      save. (Crashloop beats overwriting good state with empty.)
- [ ] **Corrupt snapshot** (deserialize throws / returns malformed) → log critical + **throw**
      (terminal; manual restore).
- [ ] **First run** (`TryReadAsync` returns `null`) → populate empty, mark loaded, write an empty
      baseline; ready.
- [ ] **Successful load** → populate **under a `_loading` guard** so repopulation does not echo
      back through `OnAdded`/`OnUpdated` as saves; mark loaded; no write-back.
- [ ] **Never save before load is confirmed** — `RequestSave()` and `SaveAsync()` both no-op
      while `!_loaded` (and `_loading`).
- [ ] **Debounce/coalesce** — N mutations within one interval produce **at most one** write.
- [ ] **Flush on shutdown** — `StoppingAsync` performs a final save (subject to the write-gate).
- [ ] **Opt-in** — an `EntityStore<T>` with no persister registered behaves exactly as today.

**Scope guards (unchanged).** v1 = whole-snapshot, single `EntityStore<T>`. Per-entity/delta
and N-stores-into-one-snapshot stay out (the latter is app composition; the former is a later
optimization — events already carry `Id<T>`). Do **not** reintroduce a bespoke `Id<T>` JSON
converter — Olve.Utilities already ships `Ids/IdOfTJsonConverterFactory.cs`.

**Do not** create the `Olve.Utilities.Hosting` package yet (v1 is in-app per the staging
above), and **do not** delete any Pipelines persistence service — those are removed only
*after* a promoted library version exists and is adopted. The implementation order is:
(1) release + bump `EntityStore` per the prerequisite above, (2) build the in-app module
against the §1.2 `Message` consumer, (3) promote to `Olve.Utilities.Hosting` once proven.

### 1.2 Rebuild the `Message` example as a real CRUD feature

The current `Message` example is in-memory ad-hoc and uses **none** of Olve.Utilities
(the package is referenced in csproj but unused — a dead reference). Replace it with a
small feature that exercises the promoted store and the underused primitives:

- `Message` record implements `IHasId<Id<Message>>` → **`Id<T>`** showcased
- backed by **`EntityStore<Message>`** (singleton) → store showcased
- `GET /messages` returns **`PaginatedResult<Message>`** → pagination showcased
- `POST/PUT/DELETE` go through an **`IHandler<TReq,TResp>`** with
  **`.WithValidation<TReq,TValidator>()`** → the underused Olve.MinimalApi handler +
  validation-filter pattern showcased
- registration wired via **`IAsyncOnStartup`** (the packaged equivalent of the
  bespoke `IRunOnStartup` pattern admired in Pipelines)

Net effect: the template's one example demonstrates `Id<T>`, `EntityStore<T>`,
`PaginatedResult<T>`, the handler/validation pattern, and startup wiring — and the
Olve.Utilities reference stops being dead.

### 1.3 Out of scope (explicitly)

- Olve.Logging — **deprecated** (use `Microsoft.Extensions.Logging` → OTLP, already wired).
- Olve.Operations — deprecated.
- Olve.Paths/Glob — add only if/when a feature needs file handling.

### 1.4 Cross-fleet backend learnings (Ralph, VisualRegression, QuestionBank, Homelab)

Surveying the other sibling backends, the honest headline is that **the template is
already ahead of them** — Ralph has no OTel, no auth, no tests, no validation, no
options pattern (all flagged as *its* gaps, not ours). So there is little to *add* and a
few things to *handle* and *avoid*.

**(a) `Id<T>` serialization is already solved — but verify under AOT.** Ralph hand-rolls
an `IdJsonConverterFactory`; this **reinvents** what Olve.Utilities already ships —
`Id<T>` carries `[JsonConverter(typeof(IdOfTJsonConverterFactory))]` and `Id` carries
`[JsonConverter(typeof(IdJsonConverter))]`
(`Olve.Utilities/src/Olve.Utilities/Ids/`). Per the dependency principle, **do not copy
Ralph's converter** — rely on the built-in (Ralph's only extra is a legacy-string
migration fallback we don't need). **However:** `IdOfTJsonConverterFactory` uses
`MakeGenericType` + `Activator.CreateInstance` (reflection), and the template is
`PublishAot=true` with a source-gen `AppJsonContext`. **Action for §1.2:** when `Message`
exposes `Id<Message>` in DTOs, add an AOT round-trip test; if the linker complains, the
fix is to ensure the closed `Id<Message>` is statically reachable via `AppJsonContext`
(not to fork the converter).

**(b) Standardize background-work wiring; avoid the fire-and-forget anti-pattern.** The
fleet shows both ends: VisualRegression uses a proper `BackgroundService`
(`CleanupService`); Ralph uses fire-and-forget `_ = MonitorAllAsync()` kicked off in
`Program.cs` (unobserved exceptions, no graceful shutdown — an anti-pattern). Template
convention: **`IAsyncOnStartup` (Olve.Utilities) for one-shot startup, `BackgroundService`
/ `IHostedService` for recurring work; never fire-and-forget.** Consider shipping one
tiny `BackgroundService` (e.g. a janitor that prunes the `EntityStore`) so real services
have a correct pattern to copy. Reinforces §1.2's startup choice.

**(c) Persistence ladder behind the `EntityStore` seam.** The template ships in-memory +
documents Testcontainers/Postgres. The fleet reveals the real spectrum, all of which
should sit *behind the store seam* (replaceable, per the dependency principle):
- in-memory `EntityStore<T>` — **template default**
- EF Core + SQLite (VisualRegression) — lightweight relational option
- S3/MinIO JSON snapshots (Pipelines) — blob/snapshot state, with `Persistent`/`Ephemeral` modes
- raw JSON-file in `~/.app/` (Ralph, QuestionBank) — **single-user only; not for the template**

Document the ladder; keep in-memory as default with a clear swap path.

**(d) Optional documented pattern — JSON polymorphism.** Ralph's
`[JsonPolymorphic]` + `[JsonDerivedType]` (`Tasks/RalphTask.cs`) is a clean,
AOT/source-gen-friendly way to serialize a domain hierarchy with a `stage`
discriminator. Worth a short "optional patterns" note for services with polymorphic
models — not core.

**(e) Explicit non-goals (seen in the fleet, deliberately not adopted):** SSH/host
execution abstractions (`IProjectHost`), file-based JSON persistence, Blazor
state/event-bus patterns, fire-and-forget startup, and channel-based log streaming
(unless a real-time feature appears — then see Ralph's `LogStreamService` for a bounded,
backpressured `Channel<T>` reference).

---

## 2. Frontend (FE)

A companion frontend that ships as an in-repo **`frontend/`** folder (resolved — see §2.6).
It is **always present, not gated by a template parameter**: a service that needs no UI
simply deletes the `frontend/` folder (and its CI/build step) rather than toggling an opt-in.
The opt-in principle in this template is about *framework/runtime lock-in* (§2.1), not about
whether the folder exists.

### 2.1 Stance

- **Vanilla Web Components, ES modules, no build step required.** Each component is a
  standalone custom element. Maximize flexibility; no framework runtime lock-in.
- **Explicit render — no automatic re-rendering in the baseline.** *(Hard constraint.)*
  The shared `BaseElement` must **not** re-render on its own. It provides only
  ergonomics; the author decides when to render.
- **Auto-rerender is per-component opt-in**, never the shared baseline.

### 2.2 `BaseElement` (the seam)

A tiny `BaseElement extends HTMLElement` that provides:

- shadow-root setup
- typed attribute get/set helpers
- a `render()` method the author **calls explicitly** (it does **not** fire from
  `attributeChangedCallback` / property setters — that would be implicit re-render)
- styling hook

What it deliberately does **not** do: schedule renders, diff, or observe state.

### 2.3 Litify-later path (opt-in)

Because every component is a standalone custom element with the same tag + public API,
a consuming project that hits real template complexity can:

- `npm i lit` and rewrite specific components to `LitElement` (auto-rerender, `lit-html`
  diffing, `@event` bindings), **or**
- swap just those components' base class to an optional reactive mixin,

…and mix vanilla + Lit components in the same app indefinitely. The template documents
this conversion as a ~per-component, ~10-minute mechanical step — not a global decision.

### 2.4 First real component

A `<message-list>` CRUD view backed by the BE `Message` feature, consuming a
**Kiota-generated TypeScript client** (the same generation path already proven in
Pipelines' frontend and offered by the backend template). This proves the
client-gen → component → API loop end-to-end.

### 2.5 Reference points in the existing repos

- `QuestionBank/packages/client/` — Vite + **vanilla TypeScript** SPA (closest to the
  no-framework stance; hand-written `fetch`).
- `Olve.Pipelines/frontend/` — Lit + Vite consuming a Kiota TS client (the
  generated-client wiring to copy; Lit usage to make optional).

### 2.6 Open questions — resolved

- ~~Separate repo vs. opt-in folder in this template?~~ **Resolved: an in-repo `frontend/`
  folder** (not a separate `Olve.Template.Web` repo).
- ~~TypeScript vs. plain JS for the vanilla baseline?~~ **Resolved: vanilla TypeScript**
  (Vite build), overriding the plain-JS recommendation below — chosen so the Kiota client is
  consumed with full types. The litify-later and no-auto-rerender stances are unchanged.
- ~~Where does the SPA get served?~~ **Resolved: same-origin from the API.** The Dockerfile
  builds `frontend/dist` into the app's `wwwroot`; the API serves the SPA at `/` (with an
  `index.html` fallback for client routing) and moves its own JSON endpoints under **`/api/`**
  (`/health` stays at the root for probes). One host, no CORS, no second deployment. A headless
  service drops the frontend by deleting `frontend/`, the Dockerfile's Node stage, and the
  `wwwroot` copy.

  *(Original recommendation, kept for the record: plain JS baseline, TS available as opt-in,
  to honor "no build step required." QuestionBank uses TS; "no build step" pushed toward JS +
  JSDoc types.)*

---

## 3. GitOps (`.pipelines/`)

### 3.1 Why

Oliver wants `.pipelines/` for **all** his apps, and `Olve.Pipelines/README.md:44`
**already points at this template** as the canonical copy-me example — but the template
ships no `.pipelines/`. The reference is currently dangling. Fixing this makes the
template deploy out of the box and makes the Pipelines doc true.

> **Homelab conformance constraint (from `Olve.Homelab`).** `Olve.Homelab` is the edge
> chart that **owns all Ingress**. Apps deploy *without* an Ingress: the template's Helm
> chart must render only a **`ClusterIP` Service** (it already does — keep it that way),
> and deployment targets the **`apps`** / **`apps-beta`** namespaces (beta gates prod).
> Public exposure (and Authentik forward-auth, cert-manager/Cloudflare TLS, path-scoped
> public routes like `/api/webhooks/...`) is configured by *adding the app's host +
> service to the edge chart's `apps[]`/`apps-beta[]` list*, not in the app's own chart.
> The template's deploy scripts/health-gate must therefore probe the in-cluster Service
> (or the edge-routed host once registered), and docs should tell the operator to add
> their entry to `Olve.Homelab`.

### 3.2 Shape (genericized from `Olve.Pipelines/.pipelines/`)

A minimal generic config — drop Pipelines-specific bits (`publish-cli`, the MinIO
secrets, the self-deploy framing):

```
.pipelines/
  config.yaml
  scripts/
    build.sh         # Kaniko build → stage image.tar + helm chart + version.txt
    test.sh          # dotnet test (code-only), parallel to build, gates deploy
    deploy-beta.sh   # import image, helm upgrade beta, health-gate prod
    deploy.sh        # helm upgrade prod
```

`config.yaml` (generic):

- `productionSteps`: `build-and-package` (kaniko) + `code-test` (dotnet sdk), in parallel
- `processingSteps`: `deploy-beta` → `deploy` (beta gates prod, sequential)
- `failureHandlers`: `aoe-triage` (built-in library handler, fires on any failure)
- `secrets`: `GITHUB_TOKEN`, `SSH_PRIVATE_KEY` only (no MinIO/CLI secrets)

### 3.3 Reuse mechanism

Pipelines' scripts already fetch a shared `olve-lib.sh` from
`raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh`
(`olve_version`, `olve_fetch_repo`, `olve_kaniko_build`, `olve_image_import`,
`olve_helm_deploy`, `olve_bundle_input`, …). The template's scripts should fetch the
**same** shared lib and only parameterize the app-specifics. So the template scripts
stay tiny.

### 3.4 Tokens to parameterize (wire into `dotnet new`)

These must be substituted by the template engine (`.template.config/template.json`)
so a new app is deploy-ready with no manual edits:

| Token | Example | Used in |
|---|---|---|
| repo slug | `OliverVea/Olve.AgentRuntimeManager` | build/deploy fetch |
| image name | `olve-agentruntimemanager` | build, helm |
| helm release | `olve-agentruntimemanager` | deploy-beta, deploy |
| namespaces | `apps-beta` / `apps` | deploy |
| health URL | `https://<app>-beta.ovea.pro/health` | beta health-gate |
| deploy host | `oliver@bulwark-m2` | ssh/import/helm |

### 3.5 Open questions

- Pin `olve-lib.sh` to a tag/SHA instead of `main` for reproducibility? (Pipelines
  uses `main`.) Recommendation: template ships `main`, documents how to pin.
- Should `slo.enabled=false` (beta) be a default override, as Pipelines does until the
  sloth CRD is cluster-wide? The template *does* ship SLO templates, so yes — mirror it.

---

## 4. Documentation

### 4.1 Problem

Current `## References` is docs-URL-only and library-scoped. It has **no GitHub links,
no skill links, and zero mention of Olve.Pipelines / the `pl` CLI / the instances** —
even though GitOps is about to become core to the template.

### 4.2 Add an "Architecture & References" section

Per-component: what it is, where it's used in the template, and the full set of links.

| Component | Docs | GitHub | Instance / tooling | Skill |
|---|---|---|---|---|
| Olve.Utilities (Results, Validation, MinimalApi, Utilities) | `olivervea.github.io/Olve.Utilities/` | `github.com/OliverVea/Olve.Utilities` | NuGet | *(none yet — gap)* |
| Olve.Pipelines (CD/GitOps) | in-repo `docs/setup/` (served at `/docs`, `llms.txt`) | `github.com/OliverVea/Olve.Pipelines` | beta `pipelines-beta.ovea.pro`, prod-private `pipelines-private.ovea.pro`, hooks `pipelines-hooks.ovea.pro`; **`pl` CLI** via instance `GET /download/{asset}` | `ovea-olve-pipelines` |
| TUnit / Rocks / Refitter / Kiota | existing links | — | — | — |

### 4.3 Document the `.pipelines/` feature

A README subsection ("Deployment / GitOps") explaining: the `.pipelines/` dir is the
single source of truth, pushing to main deploys, beta gates prod, secrets are by-name,
and the `pl` CLI inspects pipelines/jobs. Link to the Pipelines docs + `ovea-olve-pipelines`
skill for the authoritative model rather than restating it.

### 4.4 README (user-facing) vs CLAUDE.md (agent-facing)

- **README**: the Architecture & References section + GitOps subsection (human onboarding).
- **CLAUDE.md**: keep its References list, add the GitHub + skill links and the
  Olve.Pipelines entry so agents working in *generated* repos know where the deploy
  model lives.

### 4.5 Optional / noted gaps

- **No Olve.Utilities skill exists** (only `ovea-olve-pipelines`). Out of scope here,
  but worth creating later for parity — the references table marks it as a gap.
- Consider mirroring Pipelines' `llms.txt` + `/docs` agent-discovery pattern if the
  template grows its own docs surface.

---

## Suggested sequencing

1. **BE first** — finish `EntityStore` edits → promote to Olve.Utilities → rebuild
   `Message` feature on top (unblocks both the FE example and a meaningful demo).
2. **GitOps** — add `.pipelines/` + template tokens (independent; makes the dangling
   Pipelines README reference true).
3. **Docs** — Architecture & References + GitOps subsection (depends on 1 & 2 existing).
4. **FE** — `BaseElement` + `<message-list>` against the new BE feature (depends on 1).
```
