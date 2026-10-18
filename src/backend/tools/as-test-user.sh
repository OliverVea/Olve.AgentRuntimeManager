#!/usr/bin/env bash
# Runs a command (the backend tests) as an unprivileged user when started as root, as the pipeline's
# gate is (`check`, in a container): a test that signals the wrong process then gets "permission
# denied" instead of killing one of root's (the container's shell, mise). Not root (a developer, an
# agent): the command runs as is, nothing changes.
#
# As root: the user `arm-test` (made if missing, with a home of its own) gets what the tests need:
# the backend's folder (its build output, where the test results go), a way to reach the toolchain
# mise installed under root's home (and the NuGet packages the build restored there: search
# permission on the folders above them, nothing more) and a temp folder of its own.
set -euo pipefail

if [ "$(id -u)" != 0 ]; then
  exec "$@"
fi

user=arm-test
backend="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! id -u "$user" >/dev/null 2>&1; then
  useradd --create-home --user-group --shell /bin/sh "$user"
fi
home="$(getent passwd "$user" | cut -d: -f6)"

# Search permission (o+x) on every folder above $1 that lacks it, so the user can reach it.
reach() {
  local dir="$1"
  while [ "$dir" != / ] && [ -n "$dir" ]; do
    # The mode's last letter is others' search: x or t when they have it, - or T when not.
    if [ -d "$dir" ]; then
      case "$(stat -c %A "$dir" | cut -c10)" in -|T) chmod o+x "$dir" ;; esac
    fi
    dir="$(dirname "$dir")"
  done
}

IFS=: read -r -a path_dirs <<< "$PATH"
for dir in "${path_dirs[@]}"; do
  case "$dir" in /*) reach "$dir" ;; esac
done
packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
reach "$packages"
if [ -n "${DOTNET_ROOT:-}" ]; then
  reach "$DOTNET_ROOT"
fi

chown -R "$user:$user" "$backend"
tmp="$(mktemp -d /tmp/arm-test-XXXXXX)"
chown "$user:$user" "$tmp"

echo "as-test-user: running as $user (uid $(id -u "$user")), not root: $*"
exec setpriv --reuid="$user" --regid="$user" --init-groups \
  env HOME="$home" USER="$user" LOGNAME="$user" TMPDIR="$tmp" NUGET_PACKAGES="$packages" "$@"
