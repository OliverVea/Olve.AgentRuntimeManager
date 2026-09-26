#!/bin/sh
# Deploy to the prod VM (runs after beta is deployed and confirmed).
# ARM runs in a libvirt VM on the homelab host (docs/OPEN-QUESTIONS.md A4). This step copies the
# built image tarball + src/deploy/vm to the host and runs vm-deploy.sh there, which ensures the
# VM exists (first run creates it), installs the release as a systemd service, and ensures the
# host relay the Olve.Homelab route targets. Idempotent; a failure stops the chain.
set -e

mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh
apk add --no-cache curl >/dev/null

HOST=oliver@bulwark-m2
olve_ssh_host bulwark-m2

INPUT_DIR=$(olve_bundle_input)
VERSION=$(cat "$INPUT_DIR/version.txt")
# The claude-code step's output (Claude Code for the agents), found by the file only it writes;
# a bundle from before that step has none.
CLAUDE_FILE=$(ls /input/*/claude-version.txt 2>/dev/null | head -1 || true)
REMOTE=olve-arm-deploy-prod

echo "Deploying olve-arm:$VERSION to the prod VM"
ssh -o StrictHostKeyChecking=no "$HOST" "rm -rf $REMOTE && mkdir -p $REMOTE"
scp -q -o StrictHostKeyChecking=no "$INPUT_DIR"/vm/* "$INPUT_DIR/image.tar" "$HOST:$REMOTE/"
CLAUDE_ARG=none
if [ -n "$CLAUDE_FILE" ]; then
  CLAUDE_DIR=$(dirname "$CLAUDE_FILE")
  ssh -o StrictHostKeyChecking=no "$HOST" "mkdir -p $REMOTE/claude-code"
  scp -q -o StrictHostKeyChecking=no "$CLAUDE_DIR/claude" "$CLAUDE_DIR/claude-version.txt" "$HOST:$REMOTE/claude-code/"
  CLAUDE_ARG=claude-code
fi
# The agents' Claude Code token goes over stdin, never on a command line.
printf '%s\n' "${CLAUDE_CODE_OAUTH_TOKEN_PROD:-}" | ssh -o StrictHostKeyChecking=no "$HOST" \
  "cd $REMOTE && bash vm-deploy.sh prod $VERSION image.tar $CLAUDE_ARG --claude-token-stdin && cd && rm -rf $REMOTE"

echo "Verifying /health via the private (Tailscale) route..."
for i in 1 2 3 4 5; do
  if ssh -o StrictHostKeyChecking=no "$HOST" "curl -skf -o /dev/null https://arm-private.ovea.pro/health"; then
    echo "prod health OK"
    exit 0
  fi
  sleep 5
done
echo "prod health check failed" >&2
exit 1
