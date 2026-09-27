#!/bin/sh
# A stand-in for `sudo` (the tests have no second user, and no sudo): checks it's called the way
# ARM calls sudo for the agent user (`-n -u <user> -- <command> …`), appends the call to
# `sudo.log` next to itself, and runs the command as the same user.
echo "$*" >> "$(dirname "$0")/sudo.log"
if [ "$1" != -n ] || [ "$2" != -u ] || [ -z "$3" ] || [ "$4" != -- ]; then
  echo "sudo-stub: unexpected call: $*" >&2
  exit 1
fi
shift 4
exec "$@"
