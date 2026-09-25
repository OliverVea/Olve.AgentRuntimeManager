# Build the SPA (src/frontend/dist) in a Node stage; it ships as static files in the app's
# wwwroot. npm ci uses the committed lockfile, and the Kiota client is committed, so no codegen
# runs here. Delete this stage (and the wwwroot COPY below) for a headless service.
FROM node:24-alpine AS frontend
WORKDIR /fe
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN npm ci
COPY src/frontend/ ./
RUN npm run build

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
COPY --from=frontend /fe/dist ./wwwroot

ENTRYPOINT ["./Olve.AgentRuntimeManager"]
