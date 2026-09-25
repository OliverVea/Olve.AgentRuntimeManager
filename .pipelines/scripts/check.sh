#!/bin/sh
# Production gate: runs `mise run ci` — the exact command a developer (or Claude) runs locally.
# It pins the toolchain (node + dotnet from mise.toml), generates the contract artifacts
# (TypeSpec → OpenAPI → TS client), then builds and tests backend (unit + contract tests),
# frontend (lint, tests, build) and CLI (typecheck, tests, binary). Runs in PARALLEL with
# build-and-package; a failure fails the production job group, which gates the processing
# cascade (deploy never runs). Produces no deploy artifacts (no version.txt), so the deploy
# scripts ignore its bundle dir.
#
# Code comes from the GitHub tarball (no .git dir), same as build.sh.
set -e

mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.AgentRuntimeManager
BRANCH=main

olve_fetch_repo "$REPO" "$BRANCH" /src
cd /src

# mise: single static binary; installs the pinned node/dotnet on first use.
curl -fsSL https://mise.run | MISE_INSTALL_PATH=/usr/local/bin/mise sh
mise trust --yes
mise install
mise run ci

echo "Checks passed"
