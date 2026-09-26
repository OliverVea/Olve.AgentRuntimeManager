#!/usr/bin/env bash
# Deploy ARM into its libvirt VM on the homelab host. Runs ON THE HOST (bulwark-m2) as a sudoer;
# the pipeline's deploy steps copy this directory plus the built image.tar over and run it.
# Idempotent end to end: the first run creates the VM, later runs only ship a new release.
#
#   vm-deploy.sh <beta|prod> <version> <image.tar>
#
# 1. ensure the VM: Ubuntu cloud image overlay + cloud-init, fixed MAC + DHCP reservation,
#    autostart (libvirt `default` NAT network)
# 2. extract /app from the image tarball (the same self-contained publish the container ships)
# 3. install it as /opt/olve-arm/releases/<version>, point `current` at it, restart the service
# 4. ensure the host relay: <Tailscale IP>:<port> → VM:5000 (systemd socket + socket-proxyd).
#    Pods can't open NEW connections into libvirt's NAT network, so the Olve.Homelab route
#    targets this relay via `hostEndpoint` instead of the VM directly.
set -euo pipefail

ENV_NAME=${1:?usage: vm-deploy.sh <beta|prod> <version> <image.tar>}
VERSION=${2:?version}
IMAGE_TAR=${3:?image.tar}
HERE=$(cd "$(dirname "$0")" && pwd)

case "$ENV_NAME" in
  beta) NAME=olve-arm-beta; MAC=52:54:00:a7:00:50; IP=192.168.122.50; RELAY_PORT=18792
        NAMESPACE=apps-beta; CA_KEY=auth-beta-ca.crt; MEMORY=4096; CPUS=2 ;;
  prod) NAME=olve-arm-prod; MAC=52:54:00:a7:00:51; IP=192.168.122.51; RELAY_PORT=18791
        NAMESPACE=apps; CA_KEY=auth-prod-ca.crt; MEMORY=8192; CPUS=4 ;;
  *) echo "unknown environment: $ENV_NAME" >&2; exit 2 ;;
esac
RELAY_IP=100.100.117.17   # the node's Tailscale IP (same pattern as agent-of-empires)
DISK_SIZE=40G
IMAGES=/var/lib/libvirt/images
BASE=$IMAGES/noble-server-cloudimg-amd64.img
BASE_URL=https://cloud-images.ubuntu.com/noble/current
VIRSH="virsh -c qemu:///system"
SSH_VM="ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ConnectTimeout=5 arm@$IP"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

log() { echo "[vm-deploy $ENV_NAME] $*"; }

ensure_base_image() {
  if sudo test -f "$BASE"; then return; fi
  log "downloading Ubuntu 24.04 cloud image"
  wget -q -O "$WORK/base.img" "$BASE_URL/noble-server-cloudimg-amd64.img"
  wget -q -O "$WORK/SHA256SUMS" "$BASE_URL/SHA256SUMS"
  (cd "$WORK" && grep ' \*noble-server-cloudimg-amd64.img$' SHA256SUMS | sed 's/noble-server-cloudimg-amd64.img/base.img/' | sha256sum -c -)
  sudo install -m 0644 "$WORK/base.img" "$BASE"
}

ensure_vm() {
  # DHCP reservation so the VM always gets $IP (ignore "already exists").
  $VIRSH net-update default add ip-dhcp-host \
    "<host mac='$MAC' name='$NAME' ip='$IP'/>" --live --config >/dev/null 2>&1 || true

  if $VIRSH dominfo "$NAME" >/dev/null 2>&1; then
    [ "$($VIRSH domstate "$NAME")" = "running" ] || $VIRSH start "$NAME" >/dev/null
    return
  fi

  ensure_base_image
  log "creating VM $NAME ($CPUS vCPU, ${MEMORY} MiB, $DISK_SIZE)"
  sudo qemu-img create -q -f qcow2 -F qcow2 -b "$BASE" "$IMAGES/$NAME.qcow2" "$DISK_SIZE"
  sed -e "s|@@NAME@@|$NAME|" -e "s|@@PUBKEY@@|$(cat ~/.ssh/id_ed25519.pub)|" \
    "$HERE/user-data.yaml" > "$WORK/user-data"
  virt-install --connect qemu:///system --name "$NAME" --memory "$MEMORY" --vcpus "$CPUS" \
    --import --disk "path=$IMAGES/$NAME.qcow2,format=qcow2" \
    --network "network=default,mac=$MAC" --osinfo ubuntu24.04 \
    --cloud-init "user-data=$WORK/user-data" --graphics none --noautoconsole >/dev/null
  $VIRSH autostart "$NAME" >/dev/null
}

wait_for_vm() {
  log "waiting for SSH + cloud-init on $IP"
  for _ in $(seq 90); do
    if $SSH_VM true 2>/dev/null; then break; fi
    sleep 5
  done
  $SSH_VM "cloud-init status --wait >/dev/null; test -d /opt/olve-arm/releases"
}

extract_app() {
  # image.tar is a `docker save`-style tarball (manifest.json + layer tarballs). Replay the
  # layers in order and keep what lands under app/.
  python3 - "$IMAGE_TAR" "$WORK/app" <<'PY'
import json, sys, tarfile, os, io
src, dest = sys.argv[1], sys.argv[2]
os.makedirs(dest, exist_ok=True)
with tarfile.open(src) as image:
    manifest = json.load(image.extractfile("manifest.json"))
    for layer in manifest[0]["Layers"]:
        data = image.extractfile(layer).read()
        with tarfile.open(fileobj=io.BytesIO(data)) as lt:
            members = [m for m in lt.getmembers()
                       if m.name.startswith("app/") and "/.wh." not in m.name]
            for m in members:
                m.name = m.name[len("app/"):]
                if m.name:
                    lt.extract(m, dest, filter="tar")
PY
  test -x "$WORK/app/Olve.AgentRuntimeManager" || { echo "app binary not found in image" >&2; exit 1; }
}

write_env() {
  cp "$HERE/env.$ENV_NAME" "$WORK/env"
  # Served by GET /api/server-info (the web UI's BETA marker).
  printf 'Arm__Version=%s\nArm__Environment=%s\n' "$VERSION" "$ENV_NAME" >> "$WORK/env"
  if [ "$ENV_NAME" = prod ]; then
    printf 'OpenTelemetry__OAuth2__ClientSecret=%s\n' \
      "$(kubectl -n apps get secret authentik-oidc-secrets -o jsonpath='{.data.otel-client-secret}' | base64 -d)" >> "$WORK/env"
  fi
  kubectl -n "$NAMESPACE" get configmap authentik-ca -o jsonpath="{.data.${CA_KEY//./\\.}}" > "$WORK/authentik-ca.crt"
}

install_release() {
  log "installing release $VERSION"
  tar -C "$WORK/app" -czf "$WORK/app.tgz" .
  scp -q -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR \
    "$WORK/app.tgz" "$WORK/env" "$WORK/authentik-ca.crt" "$HERE/olve-arm.service" "arm@$IP:/tmp/"
  $SSH_VM "set -e
    rel=/opt/olve-arm/releases/$VERSION
    rm -rf \$rel && mkdir -p \$rel && tar -C \$rel -xzf /tmp/app.tgz
    ln -sfn \$rel /opt/olve-arm/current
    sudo install -m 0600 -o arm /tmp/env /etc/olve-arm/env
    sudo install -m 0644 /tmp/authentik-ca.crt /usr/local/share/ca-certificates/authentik-ca.crt
    sudo update-ca-certificates >/dev/null
    sudo install -m 0644 /tmp/olve-arm.service /etc/systemd/system/olve-arm.service
    sudo systemctl daemon-reload
    sudo systemctl enable olve-arm >/dev/null 2>&1
    sudo systemctl restart olve-arm
    rm -f /tmp/app.tgz /tmp/env
    ls -1dt /opt/olve-arm/releases/* | tail -n +4 | xargs -r rm -rf   # keep 3 releases"
  for _ in $(seq 60); do
    curl -fs "http://$IP:5000/health" >/dev/null && { log "healthy on $IP:5000"; return; }
    sleep 2
  done
  $SSH_VM "sudo journalctl -u olve-arm -n 50 --no-pager" >&2 || true
  echo "ARM did not become healthy in $NAME" >&2
  exit 1
}

ensure_relay() {
  local unit=olve-arm-$ENV_NAME-relay
  cat > "$WORK/$unit.socket" <<EOF
[Unit]
Description=Relay $RELAY_IP:$RELAY_PORT to the $NAME VM
[Socket]
ListenStream=$RELAY_IP:$RELAY_PORT
FreeBind=true
[Install]
WantedBy=sockets.target
EOF
  cat > "$WORK/$unit.service" <<EOF
[Unit]
Description=Relay to the $NAME VM
Requires=$unit.socket
After=$unit.socket
[Service]
ExecStart=/lib/systemd/systemd-socket-proxyd $IP:5000
EOF
  local changed=0
  for f in "$unit.socket" "$unit.service"; do
    if ! sudo cmp -s "$WORK/$f" "/etc/systemd/system/$f"; then
      sudo install -m 0644 "$WORK/$f" "/etc/systemd/system/$f"; changed=1
    fi
  done
  if [ $changed = 1 ]; then
    sudo systemctl daemon-reload
    sudo systemctl stop "$unit.service" 2>/dev/null || true
    sudo systemctl enable "$unit.socket" >/dev/null 2>&1
    sudo systemctl restart "$unit.socket"
  fi
  curl -fs "http://$RELAY_IP:$RELAY_PORT/health" >/dev/null && log "relay OK on $RELAY_IP:$RELAY_PORT"
}

ensure_vm
wait_for_vm
extract_app
write_env
install_release
ensure_relay
log "deployed $VERSION"
