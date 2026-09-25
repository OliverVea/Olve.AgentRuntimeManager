#!/bin/sh
# Run the unit test suite as a production step in PARALLEL with build-and-package — not
# inside the Docker build, so tests and image build run concurrently instead of serialized.
# A failure here fails the production job group, which gates the whole processing cascade
# (deploy never runs).
#
# Code comes from the GitHub tarball, the same way build.sh fetches it (the runner has no
# git checkout). The tarball is `git archive`-equivalent: source only, no .git dir — so
# tests must not depend on .git to locate the repo root.
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

# Unit suite only: RunUnitTests is on by default. The integration tests need Docker
# (Testcontainers) and are not run here — they exercise the AOT image locally/in CI.
dotnet test /src/src/backend/Olve.AgentRuntimeManager.UnitTests/Olve.AgentRuntimeManager.UnitTests.csproj -c Release

echo "Tests passed"
