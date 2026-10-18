#!/usr/bin/env bash
# Runs a command (the backend tests) as an unprivileged user when started as root, as the pipeline's
# gate is (`check`, in a container): a test that signals the wrong process then gets "permission
# denied" instead of killing one of root's (the container's shell, mise). Not root (a developer, an
# agent): the command runs as is, nothing changes.
#
# As root: the user `arm-test` (made if missing, with a home of its own) gets what the tests need:
# the backend's folder (its build output, where the test results go) and a way to reach the
# toolchain mise installed under root's home (and the NuGet packages the build restored there:
# search permission on the folders above them, nothing more). Its temp folder is /tmp (shared,
# sticky): a folder of its own there would leave the tests' socket paths too little room.
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

# Search permission (o+x) on every folder above $1, and above where it leads if it's a link, that
# lacks it, so the user can reach it. Best effort: a folder that can't be read or changed (read-only)
# is warned about and left; if the tests then can't reach their toolchain, they fail saying so.
reach() {
  local dir
  for dir in "$1" "$(readlink -f "$1" || echo "$1")"; do
    while [ "$dir" != / ] && [ -n "$dir" ]; do
      # The mode's last letter is others' search: x or t when they have it, - or T when not.
      if [ -d "$dir" ] && [ ! -L "$dir" ]; then
        case "$(stat -c %A "$dir" 2>/dev/null | cut -c10)" in
          -|T) chmod o+x "$dir" || echo "as-test-user: warning: could not give $user search permission on $dir" >&2 ;;
        esac
      fi
      dir="$(dirname "$dir")"
    done
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

echo "as-test-user: running as $user (uid $(id -u "$user")), not root: $*"
exec setpriv --reuid="$user" --regid="$user" --init-groups \
  env HOME="$home" USER="$user" LOGNAME="$user" TMPDIR=/tmp NUGET_PACKAGES="$packages" "$@"
