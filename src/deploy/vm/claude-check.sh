#!/usr/bin/env bash
# Beta confirmation for the `claude` provider (docs/OPEN-QUESTIONS.md A10): one real Claude Code
# turn, once per bundle, run IN the VM as `arm` by .pipelines/scripts/deploy-beta.sh. Uses the
# release's own `claude`, the token from /etc/olve-arm/env, and the provider's lockdown (keep the
# flags and environment in step with ClaudeProvider.StartInfo). Fails unless the agent saw no
# tools, MCP servers, user plugins or skills, and the turn succeeded. Costs one Haiku turn.
set -euo pipefail

CLAUDE=/opt/olve-arm/current/claude
TOKEN=$(sed -n 's/^CLAUDE_CODE_OAUTH_TOKEN=//p' /etc/olve-arm/env)
[ -n "$TOKEN" ] || { echo "no Claude Code token in /etc/olve-arm/env" >&2; exit 1; }

DIR=$(mktemp -d)
trap 'rm -rf "$DIR"' EXIT
cd "$DIR"

# One user message, then end of input: the agent answers one turn and exits.
printf '%s\n' '{"type":"user","message":{"role":"user","content":"Tell me a joke"},"parent_tool_use_id":null}' |
  env -i PATH=/usr/bin:/bin HOME="$HOME" CLAUDE_CODE_OAUTH_TOKEN="$TOKEN" \
    ENABLE_CLAUDEAI_MCP_SERVERS=false CLAUDE_CODE_DISABLE_AUTO_MEMORY=1 DISABLE_UPDATES=1 \
    timeout 120 "$CLAUDE" -p --verbose --input-format stream-json --output-format stream-json \
      --session-id "$(cat /proc/sys/kernel/random/uuid)" \
      --tools "" --permission-prompts none \
      --setting-sources "" --strict-mcp-config --disable-slash-commands \
      --model haiku > output.jsonl 2> stderr.log \
  || { echo "claude failed:" >&2; cat stderr.log >&2; exit 1; }

python3 - output.jsonl <<'PY'
import json, sys
events = [json.loads(line) for line in open(sys.argv[1]) if line.strip()]
init = next((e for e in events if e.get("type") == "system" and e.get("subtype") == "init"), None)
result = next((e for e in events if e.get("type") == "result"), None)
problems = []
if init is None:
    problems.append("no init event")
else:
    if init.get("tools"): problems.append(f"tools: {init['tools']}")
    if init.get("mcp_servers"): problems.append(f"mcp_servers: {init['mcp_servers']}")
    if init.get("skills"): problems.append(f"skills: {init['skills']}")
    user_plugins = [p for p in init.get("plugins", []) if not str(p.get("source", "")).endswith("@builtin")]
    if user_plugins: problems.append(f"plugins: {user_plugins}")
if result is None:
    problems.append("no result event")
elif result.get("is_error") or result.get("subtype") != "success":
    problems.append(f"turn ended with {result.get('subtype')}: {result.get('errors') or result.get('result')}")
if problems:
    print("Claude Code check FAILED: " + "; ".join(problems), file=sys.stderr)
    sys.exit(1)
print(f"Claude Code {init.get('claude_code_version')} ({init.get('model')}) locked down; it says: {result.get('result')!r}")
PY
