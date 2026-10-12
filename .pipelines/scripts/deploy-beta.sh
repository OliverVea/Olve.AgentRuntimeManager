#!/bin/sh
# Deploy to the beta VM and gate prod.
# ARM runs in a libvirt VM on the homelab host (docs/OPEN-QUESTIONS.md A4). This step copies the
# built image tarball + src/deploy/vm to the host and runs vm-deploy.sh there, which ensures the
# VM exists (first run creates it), installs the release as a systemd service, and ensures the
# host relay the Olve.Homelab route targets, then runs ARM's beta checks: real sessions through ARM
# that prove its main paths end to end (src/deploy/vm/beta-check.py). Idempotent; a failure stops
# the chain, so prod never deploys.
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

# ARM's beta checks, in the VM as arm (src/deploy/vm/beta-check.py): real sessions through ARM,
# three Haiku turns per bundle (none when a retried deploy's version already passed), each check
# saying what it proved or what failed. The machine client's secret travels inside the script on
# stdin (a PRESET in front of it), never on a command line or in an environment. Bounded: the
# checks' own waits, the VM's `timeout` for the whole run (about a minute when all is well), and
# ssh keepalives for the hops. A bundle from before the beta checks gets its own check (one joke
# session, claude-check.sh); one from before Claude Code was deployed has none (nor Claude Code).
SSH_BOUNDED="-o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=4"
VM_SSH="ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR $SSH_BOUNDED arm@192.168.122.50"
SECRET_B64=""
if [ -n "$CLAUDE_FILE" ]; then
  # Checked first: inside the pipe below, a missing one wouldn't stop the step.
  [ -n "${ARM_BETA_OIDC_CLIENT_SECRET:-}" ] || { echo "ARM_BETA_OIDC_CLIENT_SECRET is not set: can't check beta" >&2; exit 1; }
  SECRET_B64=$(printf '%s' "$ARM_BETA_OIDC_CLIENT_SECRET" | base64 | tr -d '\n')
  case "$VERSION" in
    ''|*[!0-9A-Za-z._-]*) echo "unexpected version '$VERSION': not checking beta" >&2; exit 1 ;;
  esac
fi
if [ -n "$CLAUDE_FILE" ] && [ -f "$INPUT_DIR/vm/beta-check.py" ]; then
  echo "Running the beta checks..."
  { printf 'PRESET = {"BETA_CHECK_VERSION": "%s", "BETA_CHECK_CLIENT_SECRET_B64": "%s"}\n' "$VERSION" "$SECRET_B64"
    cat "$INPUT_DIR/vm/beta-check.py"; } |
    ssh -o StrictHostKeyChecking=no $SSH_BOUNDED "$HOST" "$VM_SSH timeout -k 15 210 python3 -u -"
elif [ -n "$CLAUDE_FILE" ] && [ -f "$INPUT_DIR/vm/claude-check.sh" ]; then
  echo "Confirming the claude provider on beta (this bundle's check)..."
  { printf 'VERSION=%s\nCLIENT_SECRET_B64=%s\n' "$VERSION" "$SECRET_B64"
    cat "$INPUT_DIR/vm/claude-check.sh"; } |
    ssh -o StrictHostKeyChecking=no $SSH_BOUNDED "$HOST" "$VM_SSH bash -s"
elif [ -n "$CLAUDE_FILE" ]; then
  # Every bundle with Claude Code has one of the two: a check gone missing must not let prod follow.
  echo "This bundle has Claude Code but no beta check (vm/beta-check.py): not confirming beta" >&2
  exit 1
fi
