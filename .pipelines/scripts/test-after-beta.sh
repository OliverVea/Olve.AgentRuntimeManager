#!/bin/sh
# Beta confirmation (processing step between deploy-beta and deploy): run the API test suite
# (src/backend/Olve.AgentRuntimeManager.ApiTests) in base-URL mode against the live beta
# Service, reached in-cluster so it doesn't depend on the ingress. Uses the same `mise` entry
# point as local runs (`mise run api:remote`). A failure fails this step, so prod never deploys.
set -e

mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.AgentRuntimeManager
BRANCH=main

olve_fetch_repo "$REPO" "$BRANCH" /src
cd /src

curl -fsSL https://mise.run | MISE_INSTALL_PATH=/usr/local/bin/mise sh
mise trust --yes
mise install

export ARM_API_BASE_URL=http://olve-arm.apps-beta.svc.cluster.local
echo "Waiting for $ARM_API_BASE_URL/health..."
for i in $(seq 30); do curl -fs "$ARM_API_BASE_URL/health" >/dev/null && break; sleep 2; done
mise run api:remote

echo "Beta confirmed"
