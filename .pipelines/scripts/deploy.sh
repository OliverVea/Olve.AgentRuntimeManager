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
REMOTE=olve-arm-deploy-prod

echo "Deploying olve-arm:$VERSION to the prod VM"
ssh -o StrictHostKeyChecking=no "$HOST" "rm -rf $REMOTE && mkdir -p $REMOTE"
scp -q -o StrictHostKeyChecking=no "$INPUT_DIR"/vm/* "$INPUT_DIR/image.tar" "$HOST:$REMOTE/"
ssh -o StrictHostKeyChecking=no "$HOST" "cd $REMOTE && bash vm-deploy.sh prod $VERSION image.tar && cd && rm -rf $REMOTE"

echo "Verifying /health via the private (Tailscale) route..."
for i in 1 2 3 4 5; do
  if ssh -o StrictHostKeyChecking=no "$HOST" "curl -skf -o /dev/null https://olve-arm-private.ovea.pro/health"; then
    echo "prod health OK"
    exit 0
  fi
  sleep 5
done
echo "prod health check failed" >&2
exit 1
