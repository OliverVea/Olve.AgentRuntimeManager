#!/usr/bin/env bash
# Beta confirmation for the `claude` provider (docs/OPEN-QUESTIONS.md A10): one real session
# through ARM, once per bundle, run IN the VM as `arm` by .pipelines/scripts/deploy-beta.sh.
#
# Expects, prepended to this script on stdin (so nothing lands on a command line):
#   VERSION=<release>            the bundle's version (the session's Idempotency-Key, so a
#                                retried deploy never starts a second session)
#   CLIENT_SECRET_B64=<base64>   the secret of ARM's confidential machine client (Auth__Audience)
#
# Gets a client-credentials token from the Authority in /etc/olve-arm/env, creates a
# claude/haiku session "Tell me a joke" (caller deploy-beta, so it shows in ARM's history), waits
# for it, and fails unless it completed and its Claude Code saw no tools, MCP servers, user
# plugins or skills (the session's init event in its output.jsonl). Costs one Haiku turn.
set -euo pipefail

: "${VERSION:?}" "${CLIENT_SECRET_B64:?}"
export VERSION CLIENT_SECRET_B64

python3 - <<'PY'
import base64, json, os, sys, time, urllib.parse, urllib.request

def env_file(path="/etc/olve-arm/env"):
    values = {}
    for line in open(path):
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.rstrip("\n").split("=", 1)
            values[key] = value
    return values

def request(url, data=None, headers=None, form=False):
    body = None
    headers = dict(headers or {})
    if data is not None:
        if form:
            body = urllib.parse.urlencode(data).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            body = json.dumps(data).encode()
            headers["Content-Type"] = "application/json"
    with urllib.request.urlopen(urllib.request.Request(url, body, headers), timeout=30) as response:
        return json.load(response)

def fail(message):
    print(f"Claude Code check FAILED: {message}", file=sys.stderr)
    sys.exit(1)

config = env_file()
authority = config["Auth__Authority"].rstrip("/") + "/"
client_id = config["Auth__Audience"]
secret = base64.b64decode(os.environ["CLIENT_SECRET_B64"]).decode().strip()
api = "http://localhost:" + config.get("Port", "5000")

discovery = request(authority + ".well-known/openid-configuration")
token = request(discovery["token_endpoint"], {
    "grant_type": "client_credentials", "client_id": client_id, "client_secret": secret, "scope": "openid",
}, form=True)["access_token"]
auth = {"Authorization": f"Bearer {token}"}

created = request(api + "/api/sessions", {
    "prompt": "Tell me a joke", "provider": "claude", "model": "haiku",
    "caller": "deploy-beta", "timeoutSeconds": 120,
}, {**auth, "Idempotency-Key": f"claude-check-{os.environ['VERSION']}"})
session_id = created["id"]
print(f"Session {session_id}")

session = request(f"{api}/api/sessions/{session_id}", headers=auth)
deadline = time.time() + 180
while session["status"] in ("queued", "working") and time.time() < deadline:
    time.sleep(2)
    session = request(f"{api}/api/sessions/{session_id}", headers=auth)
if session["status"] != "completed":
    fail(f"session ended {session['status']}: {session.get('error') or session.get('killReason') or ''}")

output = f"/var/lib/olve-arm/sessions/{session_id}/output.jsonl"
events = [json.loads(line) for line in open(output) if line.strip()]
init = next((e for e in events if e.get("type") == "system" and e.get("subtype") == "init"), None)
if init is None:
    fail("no init event in " + output)
problems = []
if init.get("tools"): problems.append(f"tools: {init['tools']}")
if init.get("mcp_servers"): problems.append(f"mcp_servers: {init['mcp_servers']}")
if init.get("skills"): problems.append(f"skills: {init['skills']}")
user_plugins = [p for p in init.get("plugins", []) if not str(p.get("source", "")).endswith("@builtin")]
if user_plugins: problems.append(f"plugins: {user_plugins}")
if problems:
    fail("; ".join(problems))
result = next((e for e in events if e.get("type") == "result"), {})
print(f"Claude Code {init.get('claude_code_version')} ({init.get('model')}) locked down; it says: {result.get('result')!r}")
PY
