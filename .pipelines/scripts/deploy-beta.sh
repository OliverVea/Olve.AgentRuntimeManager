#!/bin/sh
# Deploy to the beta VM and gate prod.
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
REMOTE=olve-arm-deploy-beta

echo "Deploying olve-arm:$VERSION to the beta VM"
ssh -o StrictHostKeyChecking=no "$HOST" "rm -rf $REMOTE && mkdir -p $REMOTE"
scp -q -o StrictHostKeyChecking=no "$INPUT_DIR"/vm/* "$INPUT_DIR/image.tar" "$HOST:$REMOTE/"
ssh -o StrictHostKeyChecking=no "$HOST" "cd $REMOTE && bash vm-deploy.sh beta $VERSION image.tar && cd && rm -rf $REMOTE"

echo "Verifying /health via the private (Tailscale) route..."
for i in 1 2 3 4 5; do
  if ssh -o StrictHostKeyChecking=no "$HOST" "curl -skf -o /dev/null https://olve-arm-beta.ovea.pro/health"; then
    echo "beta health OK"
    exit 0
  fi
  sleep 5
done
echo "beta health check failed" >&2
exit 1
