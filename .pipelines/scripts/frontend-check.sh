#!/bin/sh
# Frontend gate: Biome lint/format + Vitest unit suite + the production build for the SPA
# in frontend/. Runs as a production step in PARALLEL with build-and-package and code-test
# (not inside them), in a Node image — the dotnet SDK image that code-test uses has no Node.
# A failure here fails the production job group, which gates the whole processing cascade
# (deploy never runs) — exactly like the .NET unit suite. Produces no deploy artifacts (no
# version.txt), so the deploy scripts ignore its bundle dir.
#
# DELETE-WITH-THE-FRONTEND: this step exists solely for the frontend/ folder — there is no
# template toggle. A headless microservice that deletes frontend/ must also delete THIS
# script and the `frontend-check` step in config.yaml. Keep both as one removable unit.
#
# Runs on node:24-alpine: busybox wget + tar (needed by olve_fetch_repo) and npm are built
# in, and package-lock.json carries the musl platform binaries for Biome and rolldown
# (Vite/Vitest), so `npm ci` resolves them. Validated against the alpine image before commit.
set -e

# Fetch the shared helper library (see build.sh for why fetch-to-file + --no-check-certificate
# and why /tmp must be created first).
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.AgentRuntimeManager
BRANCH=main

olve_fetch_repo "$REPO" "$BRANCH" /src

cd /src/src/frontend

# Clean, reproducible install from the committed lockfile, then the three gates: Biome
# (lint + format check), the Vitest unit suite, and the tsc typecheck + Vite production build.
npm ci
npm run lint
npm test
npm run build

echo "Frontend checks passed"
