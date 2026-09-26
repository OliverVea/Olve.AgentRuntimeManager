#!/usr/bin/env bash
# A stand-in for `claude` (docs/TESTING.md L2): replays recorded stream-json output, chosen by a
# `stub:<behaviour>` word in the prompt. Writes how it was started (arguments one per line,
# then the environment) to `invocation.txt` in its working directory.
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
{ printf '%s\n' "$@" | sed 's/^/arg:/'; env | sort | sed 's/^/env:/'; } > invocation.txt

# The prompt arrives as the first stdin line, like with the real CLI.
IFS= read -r prompt || exit 0
printf '%s\n' "$prompt" > prompt.jsonl

case "$prompt" in
  *stub:crash*) echo "Invalid API key · Please run /login" >&2; exit 3 ;;
  *stub:hang*) exec sleep 3600 ;;
  *stub:error*) cat "$here/error.jsonl" ;;
  # The API refusing the first request (captured against a local stub API, 2.1.283).
  *stub:unauthorized*) cat "$here/unauthorized.jsonl" ;;
  *stub:limited*) cat "$here/limited.jsonl" ;;
  *stub:overloaded*) cat "$here/overloaded.jsonl" ;;
  *stub:server-error*) cat "$here/server-error.jsonl" ;;
  *stub:offline*) cat "$here/offline.jsonl" ;;
  *) cat "$here/success.jsonl" ;;
esac

case "$prompt" in
  *stub:linger*) exec sleep 3600 ;;
esac

# Like the CLI: done once its input is closed.
cat > /dev/null
