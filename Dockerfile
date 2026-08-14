# Build the SPA (frontend/dist) in a Node stage; it ships as static files in the app's
# wwwroot. npm ci uses the committed lockfile, and the Kiota client is committed, so no codegen
# runs here. Delete this stage (and the wwwroot COPY below) for a headless service.
FROM node:24-alpine AS frontend
WORKDIR /fe
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Olve.AgentRuntimeManager/Olve.AgentRuntimeManager.csproj src/Olve.AgentRuntimeManager/
RUN dotnet restore src/Olve.AgentRuntimeManager -r linux-x64

COPY src/Olve.AgentRuntimeManager/ src/Olve.AgentRuntimeManager/
RUN dotnet publish src/Olve.AgentRuntimeManager -c Release -r linux-x64 -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# Static SPA served at / by UseStaticFiles + the index fallback (see Program.cs).
COPY --from=frontend /fe/dist ./wwwroot

ENTRYPOINT ["./Olve.AgentRuntimeManager"]
