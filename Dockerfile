# Build the SPA (src/frontend/dist) in a Node stage; it ships as static files in the app's
# wwwroot. The repo root is an npm workspace, so this stage installs from the root lockfile and
# the frontend build generates its API client from the contract first
# (src/spec/main.tsp → artifacts/spec/openapi.json → Hey API → artifacts/clients/ts → vite).
# Delete this stage (and the wwwroot COPY below) for a headless service.
FROM node:24-alpine AS frontend
WORKDIR /repo
# Every workspace's package.json must be present for `npm ci` to match the lockfile.
COPY package.json package-lock.json ./
COPY src/frontend/package.json src/frontend/
COPY src/cli/package.json src/cli/
# --ignore-scripts: the SPA build doesn't need the bun/mise binaries those scripts download.
RUN npm ci --ignore-scripts
COPY tspconfig.yaml openapi-ts.config.mjs ./
COPY src/spec/ src/spec/
COPY src/frontend/ src/frontend/
RUN npm run build --workspace src/frontend

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src

COPY src/backend/Directory.Build.props src/backend/Directory.Packages.props src/backend/
COPY src/backend/Olve.AgentRuntimeManager/Olve.AgentRuntimeManager.csproj src/backend/Olve.AgentRuntimeManager/
RUN dotnet restore src/backend/Olve.AgentRuntimeManager -r linux-x64

COPY src/backend/Olve.AgentRuntimeManager/ src/backend/Olve.AgentRuntimeManager/
RUN dotnet publish src/backend/Olve.AgentRuntimeManager -c Release -r linux-x64 -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# Static SPA served at / by UseStaticFiles + the index fallback (see Program.cs).
COPY --from=frontend /repo/src/frontend/dist ./wwwroot

ENTRYPOINT ["./Olve.AgentRuntimeManager"]
