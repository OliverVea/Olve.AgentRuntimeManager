#!/bin/sh
# Deploy to the beta VM and gate prod.
# ARM runs in a libvirt VM on the homelab host (docs/OPEN-QUESTIONS.md A4). This step copies the
# built image tarball + src/deploy/vm to the host and runs vm-deploy.sh there, which ensures the
# VM exists (first run creates it), installs the release as a systemd service, and ensures the
# host relay the Olve.Homelab route targets, then confirms the `claude` provider with one real
# session through ARM (src/deploy/vm/claude-check.sh). Idempotent; a failure stops the chain.
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
REMOTE=olve-arm-deploy-beta

echo "Deploying olve-arm:$VERSION to the beta VM"
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
printf '%s\n' "${CLAUDE_CODE_OAUTH_TOKEN_BETA:-}" | ssh -o StrictHostKeyChecking=no "$HOST" \
  "cd $REMOTE && bash vm-deploy.sh beta $VERSION image.tar $CLAUDE_ARG --claude-token-stdin && cd && rm -rf $REMOTE"

echo "Verifying /health via the private (Tailscale) route..."
healthy=0
for i in 1 2 3 4 5; do
  if ssh -o StrictHostKeyChecking=no "$HOST" "curl -skf -o /dev/null https://arm-beta.ovea.pro/health"; then
    echo "beta health OK"
    healthy=1
    break
  fi
  sleep 5
done
[ "$healthy" = 1 ] || { echo "beta health check failed" >&2; exit 1; }

# One real claude session through ARM per bundle (a Haiku joke; it shows in beta's history), run
# in the VM. The machine client's secret travels inside the script on stdin, never on a command
# line. Bundles from before Claude Code was deployed have no check (and no Claude Code).
if [ -n "$CLAUDE_FILE" ] && [ -f "$INPUT_DIR/vm/claude-check.sh" ]; then
  echo "Confirming the claude provider on beta..."
  { printf 'VERSION=%s\nCLIENT_SECRET_B64=%s\n' "$VERSION" "$(printf '%s' "${ARM_BETA_OIDC_CLIENT_SECRET:?}" | base64 | tr -d '\n')"
    cat "$INPUT_DIR/vm/claude-check.sh"; } |
    ssh -o StrictHostKeyChecking=no "$HOST" \
      "ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR arm@192.168.122.50 bash -s"
fi
