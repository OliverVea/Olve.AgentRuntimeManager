#!/usr/bin/env bash
# Node and uv for the agents, IN the VM, as root (vm-deploy.sh pipes it to `sudo bash -s` on every
# deploy): external MCP servers (M10c) run as `npx -y pkg@1.2.3` or `uvx pkg==1.2.3`, and download
# on first use. Pinned here, each checked against its publisher's SHA-256, installed once per
# version under /opt/olve-arm/toolchain and linked into /usr/local/bin, which is on the agents'
# PATH (ARM's service keeps systemd's default). Versions no longer pinned are removed.
# TOOLCHAIN_ROOT and TOOLCHAIN_BIN move both places, to try it outside a VM.
set -euo pipefail
cd /

NODE_VERSION=24.22.2
NODE_SHA256=48cc0e21e048790e54c0cf5749c2725005313d652651c40b7a21feb9697aaf75
UV_VERSION=0.13.6
UV_SHA256=f29c3e94ffabd7e6f3ba521dba169a9b7ec12100bf3257087f0f24f6123f3645

root=${TOOLCHAIN_ROOT:-/opt/olve-arm/toolchain}
bin=${TOOLCHAIN_BIN:-/usr/local/bin}
node=$root/node-v$NODE_VERSION
uv=$root/uv-$UV_VERSION

# <url> <sha256> <folder>: the archive's top folder becomes <folder>; marked done last, so an
# install cut short is made again.
install_archive() {
  local url=$1 sum=$2 dest=$3 tmp
  [ -e "$dest/.installed" ] && return 0
  echo "[toolchain] installing $(basename "$dest")"
  tmp=$(mktemp -d)
  curl -fsSL --retry 3 -o "$tmp/archive" "$url"
  echo "$sum  $tmp/archive" | sha256sum -c --quiet -
  mkdir "$tmp/unpacked"
  tar -C "$tmp/unpacked" --strip-components=1 --no-same-owner -xf "$tmp/archive"
  rm -rf "$dest"
  mkdir -p "$root"
  mv "$tmp/unpacked" "$dest"
  chmod 0755 "$dest"
  touch "$dest/.installed"
  rm -rf "$tmp"
}

install_archive "https://nodejs.org/dist/v$NODE_VERSION/node-v$NODE_VERSION-linux-x64.tar.xz" "$NODE_SHA256" "$node"
install_archive "https://github.com/astral-sh/uv/releases/download/$UV_VERSION/uv-x86_64-unknown-linux-gnu.tar.gz" "$UV_SHA256" "$uv"

mkdir -p "$bin"
for tool in node npm npx; do ln -sfn "$node/bin/$tool" "$bin/$tool"; done
for tool in uv uvx; do ln -sfn "$uv/$tool" "$bin/$tool"; done

for old in "$root"/*; do
  [ "$old" = "$node" ] || [ "$old" = "$uv" ] || rm -rf "$old"
done
