#!/usr/bin/env bash
# The pipeline gate's checks (`mise run ci`), locally, for what changed.
#
#   tools/check.sh            every check (= mise run ci)
#   tools/check.sh --changed  only the checks the changes against the upstream branch (and uncommitted
#                             work) can break: docs and mocks need none; the contract, the emitter and
#                             the toolchain need everything
#   tools/check.sh --hook     --changed, quiet on success, failures on stderr, exit 2 (the push hooks)
set -u
cd "$(dirname "$0")/.." || exit 2

hook=0 changed=0
for a in "$@"; do
  case "$a" in
    --hook) hook=1; changed=1 ;; --changed) changed=1 ;;
    *) echo "usage: tools/check.sh [--changed] [--hook]" >&2; exit 2 ;;
  esac
done

say() { [ $hook = 1 ] || echo "$@"; }

declare -A want=([all]=1)
if [ $changed = 1 ]; then
  base=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null || echo origin/main)
  files=$( { git diff --name-only "$base" -- ; git ls-files --others --exclude-standard; } | sort -u)
  want=()
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    case "$f" in
      docs/*|*.md|src/frontend/mocks/*|.claude/*) ;;
      src/spec/*|src/codegen/*|mise.toml|package.json|package-lock.json) want[all]=1 ;;
      src/backend/*) want[backend]=1 ;;
      # The beta check's own tests are in the backend's API tests.
      .pipelines/*) want[backend]=1; want[pipeline]=1 ;;
      src/frontend/*) want[frontend]=1 ;;
      src/cli/*) want[cli]=1 ;;
      *) want[all]=1 ;;
    esac
  done <<<"$files"
  if [ ${#want[@]} = 0 ]; then
    say "nothing to check against $base (docs and mocks only)"
    exit 0
  fi
  say "changed against $base: ${!want[*]}"
fi

log=$(mktemp -d); trap 'rm -rf "$log"' EXIT
failed=()
run() { # run <name> <command...>
  local name=$1; shift
  local out="$log/${name//[^a-zA-Z0-9]/_}.log"
  if "$@" >"$out" 2>&1; then
    say "ok    $name"
  else
    failed+=("$name")
    echo "FAIL  $name" >&2
    tail -n 40 "$out" | sed 's/^/      /' >&2
  fi
}

if [ -n "${want[all]:-}" ]; then
  run ci npx mise run ci
else
  [ -d node_modules ] || run install npx mise run install
  run generate npx mise run generate
  [ -z "${want[backend]:-}" ] || run backend npx mise run backend:test
  [ -z "${want[frontend]:-}" ] || run frontend npx mise run frontend:check
  [ -z "${want[cli]:-}" ] || run cli npx mise run cli:check
  [ -z "${want[pipeline]:-}" ] || ! command -v pl >/dev/null || run pipeline pl dev validate
fi

if [ ${#failed[@]} -gt 0 ]; then
  echo "check failed: ${failed[*]} (tools/check.sh --changed to rerun)" >&2
  exit 2
fi
say "all checks passed"
