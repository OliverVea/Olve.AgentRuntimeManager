#!/usr/bin/env bash
# A stand-in for `claude` (docs/TESTING.md L2): replays recorded stream-json output, chosen by a
# `stub:<behaviour>` word in the prompt. Writes how it was started (arguments one per line,
# then the environment) to `invocation.txt` in its working directory. `stub:gate` holds the
# output back until a `release` file appears in the working directory, `stub:gate-exit` then also
# exits without waiting for its input to close (gentle-restart tests). Every stdin line it reads
# is kept in `input.jsonl`. `stub:turns` replays a recorded run of two messages (two-messages.jsonl,
# 2.1.283, `--replay-user-messages`): a turn per message, the second once a second message comes,
# each replay carrying the uuid of the message it was given, like the CLI; `stub:slow` makes the
# second turn take a second.
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# `claude auth status --json`: logged in with a token, or a login in the configuration folder
# (where Claude Code keeps it, `.credentials.json`).
if [ "$1" = auth ]; then
  if [ -n "${CLAUDE_CODE_OAUTH_TOKEN:-}" ] || [ -f "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/.credentials.json" ]; then
    echo '{"loggedIn": true, "authMethod": "oauth_token"}'
  else
    echo '{"loggedIn": false, "authMethod": "none"}'; exit 1
  fi
  exit 0
fi
{ printf '%s\n' "$@" | sed 's/^/arg:/'; env | sort | sed 's/^/env:/'; } > invocation.txt

# The prompt arrives as the first stdin line, like with the real CLI.
IFS= read -r prompt || exit 0
printf '%s\n' "$prompt" > prompt.jsonl
printf '%s\n' "$prompt" >> input.jsonl

uuid_of() { printf '%s' "$1" | sed -n 's/.*"uuid":"\([^"]*\)".*/\1/p'; }
# Lines $1 of the two-message recording, its replay placeholder $2 as the uuid $3.
recorded() { sed -n "$1p" "$here/two-messages.jsonl" | sed "s/$2/$3/"; }

case "$prompt" in
  *stub:gate*) while [ ! -f release ]; do sleep 0.05; done ;;
esac

case "$prompt" in
  *stub:crash*) echo "Invalid API key · Please run /login" >&2; exit 3 ;;
  *stub:hang*) exec sleep 3600 ;;
  *stub:error*) cat "$here/error.jsonl" ;;
  # The API refusing the first request (captured against a local stub API, 2.1.283).
  *stub:unauthorized*) cat "$here/unauthorized.jsonl" ;;
  *stub:not-logged-in*) cat "$here/not-logged-in.jsonl" ;;
  *stub:limited*) cat "$here/limited.jsonl" ;;
  *stub:overloaded*) cat "$here/overloaded.jsonl" ;;
  *stub:server-error*) cat "$here/server-error.jsonl" ;;
  *stub:offline*) cat "$here/offline.jsonl" ;;
  *stub:turns*)
    recorded 1,6 11111111-1111-4111-8111-111111111111 "$(uuid_of "$prompt")"
    IFS= read -r next || exit 0
    printf '%s\n' "$next" >> input.jsonl
    recorded 7,8 22222222-2222-4222-8222-222222222222 "$(uuid_of "$next")"
    case "$prompt" in *stub:slow*) sleep 1 ;; esac
    recorded 9,11 - - ;;
  *) cat "$here/success.jsonl" ;;
esac

case "$prompt" in
  # Doesn't exit once its input is closed (marked by an `input-closed` file).
  *stub:linger*) cat >> input.jsonl; : > input-closed; exec sleep 3600 ;;
  *stub:gate-exit*) exit 0 ;;
esac

# Like the CLI: done once its input is closed.
cat >> input.jsonl
