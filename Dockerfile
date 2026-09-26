# Node stage: compiles the contract and builds the SPA (src/frontend/dist, shipped as static files
# in the app's wwwroot). The repo root is an npm workspace, so this stage installs from the root
# lockfile, and the frontend build compiles the contract first: src/spec/main.tsp →
# artifacts/spec/openapi.json → Hey API → artifacts/clients/ts → vite, and (our own emitter,
# src/codegen/typespec-arm-csharp) → artifacts/generated/backend/*.g.cs, which the .NET stage
# below compiles (it has no Node).
FROM node:24-alpine AS frontend
WORKDIR /repo
# Every workspace's package.json must be present for `npm ci` to match the lockfile.
COPY package.json package-lock.json ./
COPY src/frontend/package.json src/frontend/
COPY src/cli/package.json src/cli/
COPY src/codegen/typespec-arm-csharp/package.json src/codegen/typespec-arm-csharp/
# --ignore-scripts: the SPA build doesn't need the bun/mise binaries those scripts download.
RUN npm ci --ignore-scripts
COPY tspconfig.yaml openapi-ts.config.mjs ./
COPY src/spec/ src/spec/
COPY src/codegen/ src/codegen/
COPY src/frontend/ src/frontend/
RUN npm run build --workspace src/frontend

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY src/backend/Directory.Build.props src/backend/Directory.Packages.props src/backend/
COPY src/backend/Olve.AgentRuntimeManager/Olve.AgentRuntimeManager.csproj src/backend/Olve.AgentRuntimeManager/
RUN dotnet restore src/backend/Olve.AgentRuntimeManager -r linux-x64

COPY src/backend/Olve.AgentRuntimeManager/ src/backend/Olve.AgentRuntimeManager/
# The API surface generated from the contract in the Node stage; SkipSpecGen compiles it as is.
COPY --from=frontend /repo/artifacts/generated/backend/ artifacts/generated/backend/
# JIT, self-contained: the runtime ships with the app, so the chiseled runtime-deps image suffices.
RUN dotnet publish src/backend/Olve.AgentRuntimeManager -c Release -r linux-x64 --self-contained -p:SkipSpecGen=true -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# Static SPA served at / by UseStaticFiles + the index fallback (see Program.cs).
COPY --from=frontend /repo/src/frontend/dist ./wwwroot

ENTRYPOINT ["./Olve.AgentRuntimeManager"]
