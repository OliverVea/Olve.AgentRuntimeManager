# frontend/ — vanilla Web Components + TypeScript

The template's companion frontend: a no-framework, **vanilla Web Components** app in
**TypeScript**, consuming the backend through a **Kiota-generated** client. It renders the
same `Message` CRUD feature the API exposes, proving the client-gen → component → API loop
end to end (DESIGN §2).

## Stance (why it looks like this)

- **Vanilla custom elements, ES modules.** Each component is a standalone `HTMLElement`. No
  React/Vue/Svelte, no always-on reactive runtime.
- **Explicit render — no automatic re-rendering.** The shared
  [`BaseElement`](src/base-element.ts) provides ergonomics only (shadow root, typed
  attribute helpers, a string-template `render()`); it never re-renders on its own.
  Components call `render()` themselves after a state change. *(Hard rule — DESIGN §2.1.)*
- **Litify-later, per component.** A component that outgrows this can `npm i lit` and switch
  *its own* base to `LitElement` for auto-rerender + diffing, while the rest stay vanilla.
  Auto-rerender is always opt-in, never the baseline.
- **TypeScript, with a build step.** DESIGN §2.6 floated plain JS ("no build step"); this
  template uses vanilla TS + Vite so the Kiota client is consumed with full types.

## Layout

```
frontend/
├─ index.html                 demo page (mounts <message-list>, optional token field)
├─ vite.config.ts             dev server + API proxy
├─ src/
│  ├─ main.ts                 entry: builds the client, defines the element, wires it
│  ├─ base-element.ts         BaseElement — the explicit-render seam + escapeHtml
│  ├─ api-client.ts           Kiota client factory (+ optional Bearer auth)
│  ├─ components/
│  │  └─ message-list.ts      <message-list> — the first real component (CRUD)
│  └─ api/                    GENERATED Kiota client (committed; see below)
```

## Run it

```bash
npm install
npm run dev        # http://localhost:5173
```

The dev server proxies `/api` to the backend (the Kiota client calls `/api/messages`). Point it
at a running API:

```bash
# against the beta pod (port-forward), matching the default proxy target:
kubectl -n apps-beta port-forward svc/olve-arm 18080:80
npm run dev

# or against a local `dotnet run`, or the tailnet host:
VITE_API_TARGET=http://localhost:5080 npm run dev
VITE_API_TARGET=https://olve-arm-beta.ovea.pro npm run dev
```

`GET /api/messages` is anonymous, so the list loads with no auth. Creating / editing / deleting
need a login — click **Log in** to run the OIDC flow (see below); the access token is then
attached as a Bearer token automatically (otherwise writes return `401`).

## Authentication (OIDC + PKCE)

Login is a hand-rolled, dependency-free **Authorization Code + PKCE** flow in
[`src/auth/oidc.ts`](src/auth/oidc.ts) (built on `fetch` + Web Crypto):

- **Config is fetched at runtime** from `GET /api/auth-config` (`{ authority, clientId, scopes }`)
  — *not* baked into the bundle, so one build serves beta and prod, each pointing at its own
  Authentik. The authority's `.well-known/openid-configuration` supplies the authorize/token URLs.
- **Log in** → PKCE challenge → redirect to Authentik → back to `/callback` → code exchanged for
  tokens. The **access token** is kept in memory; the **refresh token** in `localStorage` so a
  reload doesn't force re-login. Tokens refresh **proactively** (before expiry) and on a **401**
  (refresh + retry once). **Log out** clears local state and ends the Authentik SSO session.
- Storage is isolated to `store()`/`load()` in `oidc.ts` — swap it (memory-only, or a
  backend-for-frontend httpOnly cookie, which is strictly safer) without touching the flow.

> **Security note.** A SPA that holds tokens in the browser is exposed to XSS exfiltration of the
> refresh token. That's the accepted SPA tradeoff; the safer option is a BFF where the backend
> holds tokens and the browser only gets an httpOnly session cookie.

### Authentik setup this needs

The SPA authenticates as a **public** (PKCE, no secret) client, separate from the confidential
client used for machine/CI tokens. In the homelab this is declared as an Authentik **blueprint**
in [`Olve.Authentik`](https://github.com/OliverVea/Olve.Authentik) (GitOps — not created by hand)
as `olve-arm-spa`, alongside the existing `olve-arm` provider. The provider:

- **Client type:** Public. **Client ID:** `olve-arm-spa` (matches `Auth__Frontend__ClientId`).
- **Redirect URIs:** `http://localhost:5173/callback` (dev) and `https://<deployed-host>/callback`.
- **Scopes / mappings:** `openid`, `email`, `profile`, `offline_access` (the last yields a refresh token).
- **Signing key:** the same certificate as the `olve-arm` provider, so the API's existing
  JWKS validates its signatures.

Its tokens carry this provider's own `iss` and an `aud` of `olve-arm-spa` — both different
from the resource provider's. The API trusts **both** issuers and **both** audiences (the SPA is
its own frontend), so no Authentik audience remapping is needed. See `Auth__Frontend__*` in the
Helm values and `AuthenticationConfiguration`.

## Build

```bash
npm run build      # tsc --noEmit (type-check) + vite build → dist/
npm run preview    # serve the built bundle
```

By default this template is **served same-origin**: the [root `Dockerfile`](../Dockerfile) has a
Node stage that runs this build and copies `dist/` into the API's `wwwroot`, so the deployed
backend serves the SPA at `/` and the JSON API at `/api/` (`GET /api/messages` is anonymous). No
base URL needed — the client uses same-origin.

To instead deploy the bundle on a *different* host than the API, set the base URL at build time:
`VITE_API_BASE_URL=https://api.example.com npm run build`.

## Test

Unit tests use [Vitest](https://vitest.dev/) with a `happy-dom` environment (custom elements
get a real shadow DOM). Tests live **next to the source** they cover (`*.test.ts`) and are
opt-in — they're not part of `npm run build`, and Vite excludes them from the bundle.

```bash
npm test          # run once
npm run test:watch
```

The shipped tests cover `BaseElement` (including the "no auto-render on attribute change"
hard rule), `escapeHtml`, and `<message-list>` against a faked Kiota client (render, empty
state, the 401 auth path, create/delete → reload).

## Lint & format

Lint *and* format are both handled by [Biome](https://biomejs.dev/) — one dependency-light
binary instead of ESLint + Prettier — configured in [`biome.json`](biome.json).

```bash
npm run lint       # check lint + format, no writes (this is the CI gate)
npm run format     # apply safe lint fixes + formatting
```

The generated client (`src/api/**`) is excluded — it isn't ours to lint. Test files may
non-null-assert the shadow DOM (`el.shadowRoot!`), so `noNonNullAssertion` is relaxed there.

## Continuous integration

Pushes are gated on the frontend: the `frontend-check` step in
[`.pipelines/config.yaml`](../.pipelines/config.yaml) runs `npm ci && npm run lint && npm test
&& npm run build` (on a Node image, in parallel with the .NET build and tests). A failure
blocks the deploy. That step and its [`scripts/frontend-check.sh`](../.pipelines/scripts/frontend-check.sh)
exist only for this folder — delete both if you strip the frontend out of the template.

## Regenerating the API client

`src/api/**` is generated by [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview)
from the backend's `../api.json` (produced on `dotnet build`). It is **committed** so the app
builds without codegen. Regenerate after an API change:

```bash
npm run generate-client
```

> **Runtime version pinning.** The generated code targets a specific `@microsoft/kiota-*`
> runtime. Kiota `1.32.5` (see `../.config/dotnet-tools.json`) pairs with the
> `1.0.0-preview.103` runtime pinned in `package.json` — a mismatched runtime changes
> serializer signatures and breaks the build. Run `dotnet tool run kiota info -d ../api.json
> -l TypeScript` to see the matching versions, and always bump the kiota CLI, regenerate, and
> bump the runtime together.
