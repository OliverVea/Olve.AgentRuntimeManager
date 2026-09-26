#!/bin/sh
# Stage Claude Code for the `claude` provider's agents (docs/OPEN-QUESTIONS.md A10). Runs in
# PARALLEL with build-and-package. Resolves the latest release once per build and puts the
# verified linux-x64 binary in the bundle, so beta and prod run the same version, deploys need
# no download, and re-promoting an older bundle rolls Claude Code back with the app.
#
# Verification mirrors the official installer, plus the manifest's GPG signature: the release
# key's fingerprint is pinned here, the manifest must be signed by it, and the binary must match
# the manifest's SHA256. Then `claude --help` must still list every flag the provider's lockdown
# uses (ClaudeProvider.StartInfo); a missing one fails the build before anything deploys.
set -eu

BASE=https://downloads.claude.ai/claude-code-releases
KEY_URL=https://downloads.claude.ai/keys/claude-code.asc
KEY_FINGERPRINT=31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE
PLATFORM=linux-x64
LOCKDOWN_FLAGS="--input-format --output-format --session-id --tools --permission-prompts --setting-sources --strict-mcp-config --disable-slash-commands --model"
OUT=${OUT:-/output}

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq --no-install-recommends ca-certificates curl gnupg jq >/dev/null

VERSION=$(curl -fsSL "$BASE/latest")
echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || { echo "unexpected latest version: $VERSION" >&2; exit 1; }
echo "Claude Code $VERSION ($PLATFORM)"

WORK=$(mktemp -d)
export GNUPGHOME="$WORK/gnupg"
mkdir -m 700 "$GNUPGHOME"
curl -fsSL "$KEY_URL" | gpg --batch --quiet --import
gpg --batch --with-colons --fingerprint | grep -q "^fpr:::::::::$KEY_FINGERPRINT:" \
  || { echo "release key fingerprint mismatch" >&2; exit 1; }

curl -fsSL -o "$WORK/manifest.json" "$BASE/$VERSION/manifest.json"
curl -fsSL -o "$WORK/manifest.json.sig" "$BASE/$VERSION/manifest.json.sig"
gpg --batch --status-fd 1 --verify "$WORK/manifest.json.sig" "$WORK/manifest.json" 2>/dev/null \
  | grep -q "^\[GNUPG:\] VALIDSIG $KEY_FINGERPRINT " \
  || { echo "manifest signature is not valid for the pinned release key" >&2; exit 1; }

CHECKSUM=$(jq -r --arg p "$PLATFORM" '.platforms[$p].checksum // empty' "$WORK/manifest.json")
[ -n "$CHECKSUM" ] || { echo "$PLATFORM not in the manifest" >&2; exit 1; }
curl -fsSL -o "$WORK/claude" "$BASE/$VERSION/$PLATFORM/claude"
echo "$CHECKSUM  $WORK/claude" | sha256sum -c --quiet -
chmod +x "$WORK/claude"

HELP=$("$WORK/claude" --help)
for flag in $LOCKDOWN_FLAGS; do
  echo "$HELP" | grep -q -- "$flag" || { echo "Claude Code $VERSION lacks $flag, which the lockdown needs" >&2; exit 1; }
done

mkdir -p "$OUT"
cp "$WORK/claude" "$WORK/manifest.json" "$WORK/manifest.json.sig" "$OUT/"
echo "$VERSION" > "$OUT/claude-version.txt"
echo "Staged Claude Code $VERSION"
