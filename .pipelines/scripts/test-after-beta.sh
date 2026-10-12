#!/bin/sh
# Beta confirmation (processing step between deploy-beta and deploy), from outside: this job talks
# to the live beta VM only over HTTP, through the host relay (no ingress dependency), like any
# client; nothing runs in the VM. A failure fails this step, so prod never deploys.
#  1. The API test suite (src/backend/Olve.AgentRuntimeManager.ApiTests) in base-URL mode, through
#     the same `mise` entry point as local runs (`mise run api:remote`).
#  2. ARM's beta checks (.pipelines/scripts/beta-check.py): real sessions through ARM's API with a
#     client-credentials token of the beta `olve-arm` machine client: one Haiku turn, the rest on the
#     `fake` provider. See docs/TESTING.md.
# Both are the bundle's own commit's, so they match the release they check.
set -e

mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.AgentRuntimeManager
INPUT_DIR=$(olve_bundle_input)
VERSION=$(cat "$INPUT_DIR/version.txt")
# The commit the bundle was built from (`<repo>@<sha>`); a bundle without one gets main's head.
REF=main
if [ -f "$INPUT_DIR/commit.txt" ]; then
  REF=$(sed 's/.*@//' "$INPUT_DIR/commit.txt")
fi

olve_fetch_repo "$REPO" "$REF" /src
cd /src

curl -fsSL https://mise.run | MISE_INSTALL_PATH=/usr/local/bin/mise sh
mise trust --yes
mise install

export ARM_API_BASE_URL=http://100.100.117.17:18792   # host relay -> beta VM
echo "Waiting for $ARM_API_BASE_URL/health..."
healthy=0
for i in $(seq 30); do
  if curl -fs --max-time 5 "$ARM_API_BASE_URL/health" >/dev/null; then healthy=1; break; fi
  sleep 2
done
[ "$healthy" = 1 ] || { echo "beta isn't healthy at $ARM_API_BASE_URL/health after 30 tries" >&2; exit 1; }
mise run api:remote

# ARM's beta checks (.pipelines/scripts/beta-check.py), from this job: every check says what it
# proved or what failed; one Haiku turn per bundle (none when a retried run's version already
# passed). The token comes from beta's Authentik (auth-beta.ovea.pro, the Authority in
# src/deploy/vm/env.beta), as the `olve-arm` confidential client; its secret stays in this job's
# environment, never on a command line. HTTPS to Authentik needs the Authentik CA, which the VM gets
# from the cluster's `authentik-ca` configMap through the host (vm-deploy.sh): read here the same
# way, a public certificate (the only thing this step asks the host; nothing runs in the VM).
# Bounded: `timeout` for the CA's read and for the whole run of the checks (about a minute when all
# is well), and the checks' own waits. A bundle from before Claude Code was deployed has no claude
# provider to check, and a commit from before these checks has none to run: each says so.
BETA_HOST=oliver@bulwark-m2
if ! ls /input/*/claude-version.txt >/dev/null 2>&1; then
  echo "This bundle ($VERSION) has no Claude Code (it predates the claude-code step): no beta checks"
elif [ ! -f .pipelines/scripts/beta-check.py ]; then
  echo "This bundle's commit ($REF) predates the post-deploy beta checks: none to run"
else
  [ -n "${ARM_BETA_OIDC_CLIENT_SECRET:-}" ] || {
    echo "ARM_BETA_OIDC_CLIENT_SECRET is not set: can't get a token to check beta (set it with pl secret set)" >&2; exit 1; }
  [ -n "${SSH_PRIVATE_KEY:-}" ] || {
    echo "SSH_PRIVATE_KEY is not set: can't read beta's Authentik CA through $BETA_HOST" >&2; exit 1; }
  PY=$(mise which python3) || { echo "No python3 from mise (mise.toml pins it): can't run the beta checks" >&2; exit 1; }
  if ! command -v ssh >/dev/null 2>&1; then
    timeout 180 sh -c 'apt-get update -qq && apt-get install -y -qq --no-install-recommends openssh-client' >/dev/null || {
      echo "Can't install the ssh client (to read beta's Authentik CA): can't check beta" >&2; exit 1; }
  fi
  mkdir -p ~/.ssh
  printf '%s\n' "$SSH_PRIVATE_KEY" > ~/.ssh/id_ed25519
  chmod 600 ~/.ssh/id_ed25519
  CA_FILE=/tmp/authentik-beta-ca.crt
  timeout -k 5 60 ssh -o StrictHostKeyChecking=no -o BatchMode=yes -o ConnectTimeout=15 "$BETA_HOST" \
    "kubectl --request-timeout=20s -n apps-beta get configmap authentik-ca -o jsonpath='{.data.auth-beta-ca\\.crt}'" > "$CA_FILE" || true
  grep -q "BEGIN CERTIFICATE" "$CA_FILE" || {
    echo "Can't read beta's Authentik CA (configMap apps-beta/authentik-ca, key auth-beta-ca.crt) through $BETA_HOST: can't check beta" >&2
    exit 1; }
  echo "Running the beta checks..."
  BETA_CHECK_API=$ARM_API_BASE_URL BETA_CHECK_VERSION=$VERSION \
  BETA_CHECK_AUTHORITY=https://auth-beta.ovea.pro/application/o/olve-arm/ BETA_CHECK_CLIENT_ID=olve-arm \
  BETA_CHECK_CLIENT_SECRET=$ARM_BETA_OIDC_CLIENT_SECRET BETA_CHECK_CA_FILE=$CA_FILE \
    timeout -k 30 240 "$PY" -u .pipelines/scripts/beta-check.py
fi

echo "Beta confirmed"
