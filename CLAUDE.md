# CLAUDE.md

See [README.md](README.md) for project structure, endpoints, configuration, CI examples, and client generation.

## Commands

```bash
dotnet build                                                # Build
dotnet test                                                 # Unit tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false  # Integration tests only
dotnet test -p:RunIntegrationTests=true                     # All tests
dotnet run --project src/Olve.AgentRuntimeManager                  # Run locally
```

## Conventions

- .NET 10, C# with file-scoped namespaces, nullable enabled, implicit usings
- Package versions managed centrally in `Directory.Packages.props` — do not add `Version` attributes in csproj files
- Local config via `dotnet user-secrets`, not appsettings files
- OpenAPI spec `api.json` is generated on build by `Microsoft.Extensions.ApiDescription.Server`

## Deployment (GitOps)

This repo deploys via **Olve.Pipelines** — the `.pipelines/` directory is the live deploy config
(single source of truth; pushing to `main` redeploys). Build+test run in parallel and gate
`deploy-beta` → `deploy` (beta gates prod). The Helm chart is **ClusterIP-only**; public exposure is
registered in the `Olve.Homelab` edge chart, not here. **Invoke the `ovea-olve-pipelines` skill** for
the authoritative model (config schema, secrets, promotion gates) before changing `.pipelines/` —
don't re-derive it. See [README.md](README.md#deployment-gitops) for the full write-up.

**Homelab deploy gotchas** (each bit us on a real deploy; all validated against Olve.Pipelines):

- **OTLP OAuth (prod only).** `otel-beta.ovea.pro` is **unauthenticated** (Tailscale) — beta clears
  `OpenTelemetry__OAuth2__TokenUrl`/`ClientId` or the app crashes at startup. Prod OTLP auths as the
  shared **`otel`** client, so its secret is the `authentik-oidc-secrets` key **`otel-client-secret`**
  (NOT `<app>-client-secret` — a different client → `invalid_grant`).
- **Authentik CA.** The chiseled base image can't validate `*.ovea.pro` TLS, so outbound HTTPS
  (prod OTLP OAuth, JWKS, OpenBao) fails with "SSL connection could not be established". Mount the
  shared `authentik-ca` configMap via `authentikCa.enabled` (`auth-prod-ca.crt`/`auth-beta-ca.crt`).
- **Routing.** No route exists until the app is added to `Olve.Homelab`'s `values-{beta,prod}.yaml`
  `apps:` list. Private/Tailscale host is `<app>-private.ovea.pro` (external-dns → `100.100.117.17`);
  the `deploy-beta` health gate probes it from the homelab node over SSH.
- **Telemetry auth is opt-in, never fatal** — empty OAuth2 config disables it (see `TelemetryConfiguration`).

## References

- [Olve.* packages](https://olivervea.github.io/Olve.Utilities/) ([GitHub](https://github.com/OliverVea/Olve.Utilities)) — index of all Olve packages
  - [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — non-throwing result types for error handling
  - [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — input validation built on Olve.Results
  - [Olve.MinimalApi](https://olivervea.github.io/Olve.Utilities/src/Olve.MinimalApi/README.html) — result-to-HTTP mapping for minimal APIs
  - [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — identifiers, collections, graph types
  - [Olve.Results.TUnit](https://olivervea.github.io/Olve.Utilities/src/Olve.Results.TUnit/README.html) — TUnit assertions for Result types (`Succeeded()`, `Failed()`, etc.)
- [Olve.Pipelines](https://github.com/OliverVea/Olve.Pipelines) — GitOps CD service; deploy model for this repo's `.pipelines/`. Skill: `ovea-olve-pipelines`. Instances: `pipelines-private.ovea.pro` (prod), `pipelines-beta.ovea.pro` (beta)
- [Olve.Homelab](https://github.com/OliverVea/Olve.Homelab) — edge chart that owns all Ingress; register an app's public host + service here, not in the app chart
- [TUnit](https://tunit.dev/docs/intro) — test framework, uses `await Assert.That(...)` fluent syntax (not xUnit/NUnit)
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — source-generated mocking (AOT-compatible)
- [Refitter](https://refitter.github.io/articles/refitter-file-format.html) — C# client source gen from OpenAPI via Refit (.refitter file format)
- [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview) — TypeScript client gen from OpenAPI
