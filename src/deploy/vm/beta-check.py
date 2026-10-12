#!/usr/bin/env python3
"""ARM's beta checks: proves a release's main paths end to end with real sessions on beta.

Run IN the beta VM as `arm` by .pipelines/scripts/deploy-beta.sh (`python3 -u -`, this file on
stdin), once per bundle; prod deploys only if it passes. Every check either passes, with one line
saying what it proved, or fails the step, saying plainly what failed; the checks after a failed one
are skipped (they build on it). Costs three model turns (Haiku): one session that runs a command
with its Bash tool (turn 1), a message that continues it (turn 2), and one killed in the middle of
a command (turn 3). Every wait is bounded.

Settings, from the environment, or from a PRESET dict deploy-beta.sh puts in front of this file
(so the secret never lands on a command line or in a process's environment):
  BETA_CHECK_VERSION            the bundle's version (required): beta must serve it, and it's the
                                record that a retried deploy has already been confirmed
  BETA_CHECK_CLIENT_SECRET_B64  the secret of ARM's confidential machine client (Auth__Audience),
                                for a client-credentials token from Auth__Authority, or
  BETA_CHECK_TOKEN              a token to use as is (the local tests)
  BETA_CHECK_ENV_FILE           ARM's config (default /etc/olve-arm/env): Port, Auth__*,
                                Providers__Claude__WorkRoot, Supervisor__User
  BETA_CHECK_ENVIRONMENT        what /api/server-info must say (default beta)
  BETA_CHECK_AGENT_USER         the user agents must run as (default Supervisor__User); it must
                                not be the check's own (ARM's), unless BETA_CHECK_SAME_USER=1
                                (a local ARM without an agent user, in the tests)
  BETA_CHECK_TURN_SECONDS       how long one turn may take (default 90)
  BETA_CHECK_KILL_SECONDS       how long a killed session's processes may take to go (default 15)
  BETA_CHECK_PROVIDER / _MODEL  default claude / haiku

Idempotent per bundle: a passing run records its version in ARM (the registered, non-default
variable BETA_CHECK_PASSED), and a run for a version already recorded starts no sessions. A run
first kills what an earlier, failed run left running. What it leaves on beta: the continued
session and (on a failure) what failed, in ARM's history; the killed one is deleted.

Adding a check: write `check_<what>(ctx) -> str` (return what it proved; raise CheckFailed with
what failed), add it to CHECKS with the number of model turns it costs, and test it in the API
tests' BetaCheckTests (a local ARM with a stub Claude Code).
"""

import base64
import json
import os
import pwd
import re
import secrets
import signal
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

CALLER = "deploy-beta"
# Marks a session as this check's (its own env, returned with it): which version started it.
TAG = "BETA_CHECK_VERSION"
# Registered as a default variable only while the first session is created (sessions take the
# registered values when they're created), so it reaches that agent and, at most, a session
# someone else creates in that moment.
NONCE_VARIABLE = "BETA_CHECK_NONCE"
# Registered, not default (it reaches no agent): the last version this check confirmed.
PASSED_VARIABLE = "BETA_CHECK_PASSED"

# What the first session runs. Its output, not the agent's words, decides: who it ran as, and
# which variables reached it (ARM's own configuration must not: Auth__Authority is in ARM's
# environment through /etc/olve-arm/env).
TOOL_COMMAND = (
    'echo "user=$(id -un) nonce=${BETA_CHECK_NONCE:-} version=${BETA_CHECK_VERSION:-} '
    'withheld=${Auth__Authority:-}"'
)
TOOL_LINE = re.compile(r"user=(?P<user>\S*) nonce=(?P<nonce>\S*) version=(?P<version>\S*) withheld=(?P<withheld>.*)")
TOOL_PROMPT = (
    "This is ARM's deploy check. Run the command below with the Bash tool, exactly as written and "
    "only once, then reply with one short sentence saying what it printed.\n\n```sh\n{command}\n```"
)
CONTINUE_TEXT = "Thanks. Now reply with just the word: done."
# The killed session's command: long enough to be killed in, marked so that its processes can be
# found even if they left the agent's process tree.
KILL_COMMAND = "sh -c 'sleep 240; echo finished' {marker}"
KILL_PROMPT = (
    "This is ARM's deploy check. Run the command below with the Bash tool, exactly as written, in "
    "the foreground (not in the background), and wait for it to finish.\n\n```sh\n{command}\n```"
)

ACTIVITY_KINDS = {"text", "tool", "error"}
ACTIVITY_MAX_LENGTH = 200
CONVERSATION_KINDS = {"prompt", "text", "thinking", "tool_call", "tool_result", "turn_end", "notice"}
ENDED = {"completed", "cancelled", "killed", "failed"}


class CheckFailed(Exception):
    """What failed, in words for the pipeline's log."""


def short(value, length=200):
    text = value if isinstance(value, str) else json.dumps(value)
    text = " ".join(text.split())
    return text if len(text) <= length else text[: length - 1] + "…"


# --- settings ---------------------------------------------------------------------------------


def read_env_file(path):
    values = {}
    with open(path) as lines:
        for line in lines:
            if "=" in line and not line.lstrip().startswith("#"):
                key, value = line.rstrip("\n").split("=", 1)
                values[key] = value
    return values


class Settings:
    def __init__(self, source):
        def get(name, default=None):
            value = source.get(name)
            return value if value not in (None, "") else default

        self.version = get("BETA_CHECK_VERSION")
        if not self.version:
            raise CheckFailed("BETA_CHECK_VERSION is not set")
        env_file = get("BETA_CHECK_ENV_FILE", "/etc/olve-arm/env")
        try:
            self.config = read_env_file(env_file)
        except OSError as error:
            raise CheckFailed(f"can't read ARM's config {env_file}: {error.strerror}")
        self.api = "http://localhost:" + self.config.get("Port", "5000")
        self.environment = get("BETA_CHECK_ENVIRONMENT", "beta")
        self.provider = get("BETA_CHECK_PROVIDER", "claude")
        self.model = get("BETA_CHECK_MODEL", "haiku")
        self.turn_seconds = int(get("BETA_CHECK_TURN_SECONDS", "90"))
        self.kill_seconds = int(get("BETA_CHECK_KILL_SECONDS", "15"))
        self.work_root = self.config.get("Providers__Claude__WorkRoot")
        if not self.work_root:
            raise CheckFailed(f"{env_file} has no Providers__Claude__WorkRoot: the sessions' folders can't be found")
        self.agent_user = get("BETA_CHECK_AGENT_USER", self.config.get("Supervisor__User"))
        if not self.agent_user:
            raise CheckFailed(f"{env_file} has no Supervisor__User: agents would run as ARM's own user")
        self.same_user = get("BETA_CHECK_SAME_USER") == "1"
        self.token = get("BETA_CHECK_TOKEN")
        self.client_secret_b64 = get("BETA_CHECK_CLIENT_SECRET_B64")
        if not self.token and not self.client_secret_b64:
            raise CheckFailed("neither BETA_CHECK_CLIENT_SECRET_B64 nor BETA_CHECK_TOKEN is set")


# --- HTTP -------------------------------------------------------------------------------------

# Straight to ARM (and the Authority): never through a proxy from the environment.
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def http(method, url, body=None, headers=None, form=False, timeout=30):
    """(status, parsed JSON or None). Never raises for an HTTP status; CheckFailed when unreachable."""
    headers = dict(headers or {})
    data = None
    if body is not None:
        if form:
            data = urllib.parse.urlencode(body).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data, headers, method=method)
    try:
        with OPENER.open(request, timeout=timeout) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    except (urllib.error.URLError, OSError) as error:
        raise CheckFailed(f"{method} {urllib.parse.urlsplit(url).path}: {getattr(error, 'reason', error)}")
    try:
        return status, json.loads(raw) if raw else None
    except ValueError:
        return status, None


def error_text(value):
    if isinstance(value, dict) and isinstance(value.get("error"), dict):
        return f" ({value['error'].get('code')}: {value['error'].get('message')})"
    return ""


class Api:
    def __init__(self, base, token):
        self.base = base
        self.auth = {"Authorization": f"Bearer {token}"}

    def call(self, method, path, body=None, expect=(200,), headers=None, auth=True):
        status, value = http(method, self.base + path, body, {**(self.auth if auth else {}), **(headers or {})})
        if status not in expect:
            raise CheckFailed(f"{method} {path} answered {status}, expected {'/'.join(map(str, expect))}{error_text(value)}")
        return value

    def stream(self, path):
        request = urllib.request.Request(self.base + path, headers={**self.auth, "Accept": "text/event-stream"})
        # Reads wait at most this long; the server sends a heartbeat every 30s.
        return OPENER.open(request, timeout=45)


def client_credentials_token(settings):
    authority = settings.config["Auth__Authority"].rstrip("/") + "/"
    status, discovery = http("GET", authority + ".well-known/openid-configuration")
    if status != 200 or not isinstance(discovery, dict) or "token_endpoint" not in discovery:
        raise CheckFailed(f"the Authority's discovery document answered {status}")
    secret = base64.b64decode(settings.client_secret_b64).decode().strip()
    status, token = http("POST", discovery["token_endpoint"], {
        "grant_type": "client_credentials", "client_id": settings.config["Auth__Audience"],
        "client_secret": secret, "scope": "openid",
    }, form=True)
    # Only the status: the answer could echo what was sent.
    if status != 200 or not isinstance(token, dict) or "access_token" not in token:
        raise CheckFailed(f"the Authority refused the client-credentials token ({status})")
    return token["access_token"]


# --- the event stream -------------------------------------------------------------------------


class Events:
    """
    `GET /api/events?event=session.*`, read for the whole run on a daemon thread, which ends with
    the process (closing the response from here would wait for the thread's read: up to a heartbeat).
    """

    def __init__(self, api):
        self._api = api
        self._lock = threading.Lock()
        self._events = []
        self._open = threading.Event()
        self.error = None

    def start(self, seconds=15):
        threading.Thread(target=self._read, daemon=True).start()
        if not self._open.wait(seconds):
            raise CheckFailed(self.error or f"the event stream sent no heartbeat within {seconds}s (it sends one on connect)")

    def of(self, session_id):
        with self._lock:
            return [e for e in self._events if e.get("sessionId") == session_id]

    def _read(self):
        try:
            response = self._api.stream("/api/events?event=session.*")
            kind = response.headers.get("Content-Type", "")
            if response.status != 200 or not kind.startswith("text/event-stream"):
                self.error = f"GET /api/events answered {response.status} {kind}"
                return
            data = []
            for raw in response:
                line = raw.decode("utf-8", "replace").rstrip("\r\n")
                if line.startswith("data:"):
                    data.append(line[5:].removeprefix(" "))
                elif line == "" and data:
                    self._dispatch("\n".join(data))
                    data = []
        except Exception as error:  # the thread's end is reported by the checks that wait on it
            self.error = self.error or f"the event stream broke: {error}"

    def _dispatch(self, data):
        try:
            event = json.loads(data)
        except ValueError:
            return
        if event.get("type") == "heartbeat":
            self._open.set()
            return
        with self._lock:
            self._events.append(event)


def event_problems(events, expected):
    """What's wrong with one session's events: `expected` is the (type, fields) to find, in order."""
    problems = []
    for event in events:
        missing = [key for key in ("type", "at", "sessionId") if not event.get(key)]
        if missing:
            problems.append(f"an event without {', '.join(missing)}: {short(event)}")
    position = 0
    for kind, fields in expected:
        while position < len(events) and not (
                events[position].get("type") == kind and all(events[position].get(k) == v for k, v in fields.items())):
            position += 1
        if position == len(events):
            wanted = kind + (f" {fields}" if fields else "")
            problems.append(f"no {wanted} where expected; got {[e.get('type') for e in events]}")
            break
        position += 1
    return problems


# --- the VM's processes -----------------------------------------------------------------------


def processes():
    """Every live process: pid → (ppid, process group, start time, uid, command line)."""
    found = {}
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        try:
            with open(f"/proc/{name}/stat") as stat_file:
                stat = stat_file.read()
            with open(f"/proc/{name}/cmdline", "rb") as cmdline_file:
                command = cmdline_file.read().replace(b"\0", b" ").decode("utf-8", "replace").strip()
            uid = os.stat(f"/proc/{name}").st_uid
        except OSError:
            continue
        # The command name (field 2) is in parentheses and may hold spaces: count from after it.
        fields = stat[stat.rindex(")") + 2:].split()
        if fields[0] in ("Z", "X"):
            continue
        found[int(name)] = (int(fields[1]), int(fields[2]), int(fields[19]), uid, command)
    return found


def session_processes(supervisor, markers):
    """
    The processes of a session: its supervisor, the process group it leads (its agent's), anything
    whose command line holds one of `markers` (the session's id, its command's marker), and all
    their descendants. pid → (start time, uid, command line).
    """
    table = processes()
    roots = {pid for pid, (_, group, _, _, command) in table.items()
             if pid == supervisor.get("supervisorPid") or group == supervisor.get("supervisorPid")
             or pid == supervisor.get("agentPid") or any(marker in command for marker in markers)}
    children = {}
    for pid, (parent, *_rest) in table.items():
        children.setdefault(parent, []).append(pid)
    found, todo = set(), list(roots)
    while todo:
        pid = todo.pop()
        if pid not in found:
            found.add(pid)
            todo.extend(children.get(pid, []))
    found.discard(os.getpid())
    return {pid: table[pid][2:] for pid in found}


def describe(found):
    return "; ".join(f"{pid} {short(command, 80)}" for pid, (_, _, command) in sorted(found.items()))


# --- what the checks share --------------------------------------------------------------------


class Context:
    def __init__(self, settings, api):
        self.settings = settings
        self.api = api
        self.events = None
        self.already_passed = False
        self.nonce = secrets.token_hex(8)
        self.nonce_registered = False
        self.created = []
        self.tool_session = None
        self.tool_prompt = TOOL_PROMPT.format(command=TOOL_COMMAND)
        self.conversation = None
        self.kill_session = None

    def folder(self, session_id):
        return os.path.join(self.settings.work_root, session_id)

    def create_session(self, prompt, timeout_seconds):
        created = self.api.call("POST", "/api/sessions", {
            "prompt": prompt, "provider": self.settings.provider, "model": self.settings.model,
            "caller": CALLER, "timeoutSeconds": timeout_seconds, "env": {TAG: self.settings.version},
        }, expect=(201, 202))
        self.created.append(created["id"])
        return created["id"]

    def session(self, session_id):
        return self.api.call("GET", f"/api/sessions/{session_id}")

    def conversation_of(self, session_id):
        return self.api.call("GET", f"/api/sessions/{session_id}/conversation")

    def refused(self, session):
        """Why the provider refused a queued session (it would retry later), or None."""
        if session["status"] != "queued" or not session.get("error"):
            return None
        health = self.api.call("GET", "/api/providers/health")
        provider = next((h for h in health if h.get("provider") == self.settings.provider), {})
        return f"the {self.settings.provider} provider refused it ({provider.get('status')}): {session['error']}"

    def wait_ended(self, session_id, seconds, done=lambda s: True):
        """The session once it has ended (and `done` holds), polled; fails after `seconds`."""
        deadline = time.monotonic() + seconds
        while True:
            session = self.session(session_id)
            if session["status"] in ENDED and done(session):
                return session
            if (reason := self.refused(session)) is not None:
                raise CheckFailed(f"session {session_id}: {reason}")
            if time.monotonic() >= deadline:
                position = f" (queue position {session.get('queuePosition')})" if session["status"] == "queued" else ""
                raise CheckFailed(f"session {session_id} is still {session['status']}{position} after {seconds}s")
            time.sleep(1)


# --- the checks ---------------------------------------------------------------------------------


def check_health(ctx):
    status, _ = http("GET", ctx.api.base + "/health")
    if status != 200:
        raise CheckFailed(f"/health answered {status}")
    return "/health is up"


def check_server_info(ctx):
    info = ctx.api.call("GET", "/api/server-info")
    if (info.get("version"), info.get("environment")) != (ctx.settings.version, ctx.settings.environment):
        raise CheckFailed(
            f"ARM says it is version {info.get('version')!r} in {info.get('environment')!r}, expected "
            f"{ctx.settings.version!r} in {ctx.settings.environment!r}: the deployed release isn't the one serving")
    return f"serving {info['version']} ({info['environment']})"


def check_auth(ctx):
    for headers, what in (({}, "no token"), ({"Authorization": "Bearer not-a-token"}, "a bad token")):
        status, _ = http("POST", ctx.api.base + "/api/sessions/search", {}, headers)
        if status != 401:
            raise CheckFailed(f"POST /api/sessions/search with {what} answered {status}, expected 401")
    return "the API refuses a request without a valid token (401)"


def check_provider_health(ctx):
    health = ctx.api.call("GET", "/api/providers/health")
    provider = next((h for h in health if h.get("provider") == ctx.settings.provider), None)
    if provider is None:
        raise CheckFailed(f"no {ctx.settings.provider} provider in {[h.get('provider') for h in health]}")
    if provider.get("status") != "available":
        until = f" until {provider['until']}" if provider.get("until") else ""
        raise CheckFailed(f"the {ctx.settings.provider} provider is {provider.get('status')}{until}: {provider.get('reason')}")
    return ", ".join(f"{h.get('provider')} {h.get('status')}" for h in health)


def check_earlier_runs(ctx):
    """A retried deploy: kill what an earlier run left running; start nothing if it already passed."""
    done = []
    page = ctx.api.call("POST", "/api/sessions/search", {"caller": CALLER, "status": ["queued", "working"], "limit": 100})
    for session in page["items"]:
        if TAG in (session.get("env") or {}):
            ctx.api.call("POST", f"/api/sessions/{session['id']}/kill",
                         {"caller": CALLER, "reason": "Left running by an earlier beta check."}, expect=(200, 409))
            done.append(f"killed session {session['id']}")
    variables = {v["name"]: v for v in ctx.api.call("GET", "/api/env")}
    if NONCE_VARIABLE in variables:
        ctx.api.call("DELETE", f"/api/env/{NONCE_VARIABLE}", expect=(204, 404))
        done.append(f"removed {NONCE_VARIABLE}")
    if variables.get(PASSED_VARIABLE, {}).get("value") == ctx.settings.version:
        ctx.already_passed = True
        done.append(f"{ctx.settings.version} already passed (an earlier run): no sessions this time")
    return "; ".join(done) or "none left over"


def open_events(ctx):
    ctx.events = Events(ctx.api)
    ctx.events.start()
    return "GET /api/events streams (heartbeat on connect)"


def bash_results(conversation):
    """(call, result) for each Bash tool call, in order; result None when it has none (yet)."""
    entries = conversation.get("entries") or []
    results = {e.get("toolId"): e for e in entries if e.get("kind") == "tool_result"}
    return [(e, results.get(e.get("toolId"))) for e in entries
            if e.get("kind") == "tool_call" and e.get("tool") == "Bash" and not e.get("parentToolId")]


def tool_output(conversation):
    """The first Bash result's labelled line, parsed; CheckFailed when there's none."""
    calls = bash_results(conversation)
    if not calls:
        tools = [e.get("tool") for e in conversation.get("entries") or [] if e.get("kind") == "tool_call"]
        raise CheckFailed(f"the agent made no Bash tool call (tool calls: {tools or 'none'})")
    for _, result in calls:
        for line in ((result or {}).get("text") or "").splitlines():
            if (match := TOOL_LINE.search(line)) is not None:
                return match.groupdict()
    raise CheckFailed(f"no Bash result has the command's 'user=… nonce=…' line: {short([r and r.get('text') for _, r in calls])}")


def lockdown(ctx, session_id):
    """Claude Code's init event: its built-in tools, but none of the machine's setup."""
    output = os.path.join(ctx.folder(session_id), "output.jsonl")
    try:
        with open(output) as lines:
            events = [json.loads(line) for line in lines if line.strip()]
    except (OSError, ValueError) as error:
        raise CheckFailed(f"can't read {output}: {error}")
    init = next((e for e in events if e.get("type") == "system" and e.get("subtype") == "init"), None)
    if init is None:
        raise CheckFailed(f"no init event in {output}")
    problems = []
    if not init.get("tools"):
        problems.append("no tools: the agent should have Claude Code's built-in tools")
    if init.get("mcp_servers"):
        problems.append(f"mcp_servers: {init['mcp_servers']}")
    if init.get("skills"):
        problems.append(f"skills: {init['skills']}")
    user_plugins = [p for p in init.get("plugins", []) if not str(p.get("source", "")).endswith("@builtin")]
    if user_plugins:
        problems.append(f"plugins: {user_plugins}")
    if problems:
        raise CheckFailed("Claude Code isn't locked down: " + "; ".join(problems))
    return f"Claude Code {init.get('claude_code_version')} ({init.get('model')}) with {len(init['tools'])} tools and nothing else loaded"


def check_tool_session(ctx):
    """Turn 1: the agent runs a command; its output says who it ran as and what reached it."""
    settings = ctx.settings
    ctx.api.call("PUT", f"/api/env/{NONCE_VARIABLE}", {"value": ctx.nonce, "default": True})
    ctx.nonce_registered = True
    try:
        ctx.tool_session = ctx.create_session(ctx.tool_prompt, settings.turn_seconds * 2 + 60)
    finally:
        ctx.api.call("DELETE", f"/api/env/{NONCE_VARIABLE}", expect=(204, 404))
        ctx.nonce_registered = False
    session = ctx.wait_ended(ctx.tool_session, settings.turn_seconds)
    if session["status"] != "completed":
        raise CheckFailed(f"session {ctx.tool_session} ended {session['status']}: {session.get('error') or session.get('killReason') or ''}")
    ctx.conversation = ctx.conversation_of(ctx.tool_session)
    seen = tool_output(ctx.conversation)
    me = pwd.getpwuid(os.getuid()).pw_name
    if seen["user"] != settings.agent_user:
        raise CheckFailed(f"the agent ran as {seen['user']!r}, expected {settings.agent_user!r}")
    if seen["user"] == me and not settings.same_user:
        raise CheckFailed(f"the agent ran as {me!r}, the user ARM (and this check) runs as")
    if seen["nonce"] != ctx.nonce:
        raise CheckFailed(f"the registered default variable {NONCE_VARIABLE} didn't reach the agent (it saw {seen['nonce']!r})")
    if seen["version"] != settings.version:
        raise CheckFailed(f"the session's own variable {TAG} didn't reach the agent (it saw {seen['version']!r})")
    if seen["withheld"].strip():
        raise CheckFailed("ARM's own configuration (Auth__Authority) reached the agent's environment")
    if settings.provider == "claude":
        locked = lockdown(ctx, ctx.tool_session)
    else:
        locked = f"provider {settings.provider}"
    return (f"session {ctx.tool_session} completed: its Bash call ran as {seen['user']} (the check runs as {me}), "
            f"the registered default and the session's own variables reached it, ARM's config didn't; {locked}")


def conversation_problems(conversation, prompts):
    """What's wrong with a finished session's conversation, given the prompts it was told in order."""
    problems = []
    entries = conversation.get("entries") or []
    if not entries:
        return ["it has no entries"]
    seqs = [e.get("seq") for e in entries]
    if seqs != list(range(1, len(entries) + 1)):
        problems.append(f"its seqs aren't 1..{len(entries)} in order: {short(seqs)}")
    if conversation.get("lastSeq") != seqs[-1]:
        problems.append(f"lastSeq is {conversation.get('lastSeq')}, its last entry's seq {seqs[-1]}")
    unknown = sorted({str(e.get("kind")) for e in entries} - CONVERSATION_KINDS)
    if unknown:
        problems.append(f"unknown kinds: {unknown}")
    told = [e.get("text") for e in entries if e.get("kind") == "prompt" and not e.get("parentToolId")]
    if told != prompts:
        problems.append(f"its prompts are {short(told)}, expected {short(prompts)}")
    if entries[0].get("kind") != "prompt":
        problems.append(f"it starts with {entries[0].get('kind')}, not the prompt")
    # Each prompt is followed by its turn's end before the next.
    turn_ends = [e for e in entries if e.get("kind") == "turn_end"]
    order = [e.get("kind") for e in entries if e.get("kind") in ("prompt", "turn_end") and not e.get("parentToolId")]
    if order != ["prompt", "turn_end"] * len(prompts):
        problems.append(f"prompts and turn ends don't alternate: {order}")
    if entries[-1].get("kind") != "turn_end" or entries[-1].get("isError"):
        problems.append(f"it doesn't end with a turn that succeeded: {short(entries[-1])}")
    counts = {
        "turns": len(turn_ends),
        "messages": len(told),
        "toolCalls": sum(e.get("kind") == "tool_call" for e in entries),
    }
    for key, count in counts.items():
        if conversation.get(key) != count:
            problems.append(f"{key} is {conversation.get(key)}, but it has {count}")
    if conversation.get("turns") != len(prompts):
        problems.append(f"turns is {conversation.get('turns')}, expected {len(prompts)}")
    calls = bash_results(conversation)
    if not calls:
        problems.append("no Bash tool call")
    else:
        call, result = calls[0]
        if not call.get("toolId") or not isinstance(call.get("input"), dict) or not call["input"].get("command"):
            problems.append(f"the Bash call has no toolId or command: {short(call)}")
        if result is None:
            problems.append("the Bash call has no result")
        elif result.get("seq", 0) < call.get("seq", 0) or result.get("isError"):
            problems.append(f"the Bash call's result is out of order or failed: {short(result)}")
        else:
            first_end = turn_ends[0].get("seq", 0) if turn_ends else 0
            if not any(e.get("kind") == "text" and e.get("text") and result["seq"] < e.get("seq", 0) < first_end
                       for e in entries):
                problems.append("no text from the agent after the Bash result in its first turn")
    return problems


def check_conversation(ctx):
    problems = conversation_problems(ctx.conversation, [ctx.tool_prompt])
    if problems:
        raise CheckFailed(f"session {ctx.tool_session}'s conversation: " + "; ".join(problems))
    c = ctx.conversation
    return f"prompt, Bash call and its result, the agent's text, the turn's end ({len(c['entries'])} entries, {c['turns']} turn, {c['toolCalls']} tool calls)"


def utf16_length(text):
    return len(text.encode("utf-16-le")) // 2


def activity_problems(lines, conversation):
    """What's wrong with a finished session's activity (`lines`: all of it, limit 50) given its conversation."""
    problems = []
    if not lines:
        return ["no lines"]
    for line in lines:
        text = line.get("text")
        if line.get("kind") not in ACTIVITY_KINDS:
            problems.append(f"a line of unknown kind: {short(line)}")
        if not isinstance(text, str) or not text.strip():
            problems.append(f"an empty line: {short(line)}")
        elif any(c in text for c in "\r\n") or any(ord(c) < 32 or ord(c) == 127 for c in text):
            problems.append(f"a line with a line break or control character: {text!r}")
        elif utf16_length(text) > ACTIVITY_MAX_LENGTH:
            problems.append(f"a line longer than {ACTIVITY_MAX_LENGTH}: {short(text)}")
    # The tools and failures, oldest first, as the conversation has them (the agent's text is
    # shaped: its first line, Markdown left out, so only its place is checked).
    expected = []
    for e in conversation.get("entries") or []:
        if e.get("parentToolId"):
            continue
        if e.get("kind") == "tool_call":
            expected.append(("tool", e.get("tool")))
        elif e.get("kind") in ("tool_result", "turn_end") and e.get("isError"):
            expected.append(("error", None))
    got = [(line.get("kind"), str(line.get("text")).split(" → ")[0] if line.get("kind") == "tool" else None)
           for line in lines if line.get("kind") != "text"]
    if len(lines) < 50 and got != expected:
        problems.append(f"its tool and error lines are {got}, the conversation's {expected}")
    tools = [i for i, line in enumerate(lines) if line.get("kind") == "tool" and str(line.get("text")).startswith("Bash → ")]
    if not tools:
        problems.append("no 'Bash → …' line")
    elif not any(line.get("kind") == "text" for line in lines[tools[0] + 1:]):
        problems.append("no line of the agent's text after its Bash call")
    return problems


def check_activity(ctx):
    path = f"/api/sessions/{ctx.tool_session}/activity"
    lines = ctx.api.call("GET", path + "?limit=50")["lines"]
    problems = activity_problems(lines, ctx.conversation)
    default = ctx.api.call("GET", path)["lines"]
    if default != lines[-3:]:
        problems.append(f"without a limit it gave {len(default)} lines, not the latest 3 of {len(lines)}")
    one = ctx.api.call("GET", path + "?limit=1")["lines"]
    if one != lines[-1:]:
        problems.append(f"limit=1 gave {short(one)}, not the latest line")
    status, _ = http("GET", ctx.api.base + path + "?limit=51", headers=ctx.api.auth)
    if status != 400:
        problems.append(f"limit=51 answered {status}, expected 400")
    if problems:
        raise CheckFailed(f"session {ctx.tool_session}'s activity: " + "; ".join(problems))
    return f"{len(lines)} bounded lines, oldest first, matching the conversation ({', '.join(l['kind'] for l in lines)}); limit honoured"


def check_continue(ctx):
    """Turn 2 (M11): a message to the ended session continues it."""
    before = ctx.session(ctx.tool_session)
    sent = ctx.api.call("POST", f"/api/sessions/{ctx.tool_session}/messages",
                        {"text": CONTINUE_TEXT, "caller": CALLER}, expect=(202,))
    if sent.get("delivery") != "continued":
        raise CheckFailed(f"the message to ended session {ctx.tool_session} was {sent.get('delivery')!r}, expected 'continued'")
    session = ctx.wait_ended(ctx.tool_session, ctx.settings.turn_seconds,
                             done=lambda s: s.get("endedAt") != before.get("endedAt"))
    if session["status"] != "completed":
        raise CheckFailed(f"the continued session {ctx.tool_session} ended {session['status']}: {session.get('error') or ''}")
    conversation = ctx.conversation_of(ctx.tool_session)
    problems = conversation_problems(conversation, [ctx.tool_prompt, CONTINUE_TEXT])
    earlier = ctx.conversation["entries"]
    if conversation.get("entries", [])[: len(earlier)] != earlier:
        problems.append("its first turn's entries changed")
    if problems:
        raise CheckFailed(f"the continued session {ctx.tool_session}'s conversation: " + "; ".join(problems))
    return (f"session {ctx.tool_session} continued and completed again: {len(earlier)} → "
            f"{len(conversation['entries'])} entries, {conversation['turns']} turns, {conversation['messages']} messages")


def read_supervisor(ctx, session_id, seconds=15):
    path = os.path.join(ctx.folder(session_id), "supervisor.json")
    deadline = time.monotonic() + seconds
    while True:
        try:
            with open(path) as info:
                return json.load(info)
        except (OSError, ValueError) as error:
            if time.monotonic() >= deadline:
                raise CheckFailed(f"can't read {path}: {error}")
        time.sleep(0.5)


def check_kill(ctx):
    """Turn 3: a session killed in the middle of a command ends `killed`, and nothing of it runs on."""
    marker = f"beta-check-{ctx.nonce}"
    command = KILL_COMMAND.format(marker=marker)
    ctx.kill_session = session_id = ctx.create_session(KILL_PROMPT.format(command=command), 180)
    # Until its agent is in a Bash call (bounded); a session that is only working is killed too.
    in_call = False
    deadline = time.monotonic() + ctx.settings.turn_seconds
    while time.monotonic() < deadline:
        session = ctx.session(session_id)
        if session["status"] in ENDED:
            raise CheckFailed(f"session {session_id} ended {session['status']} on its own before it could be killed "
                              f"(its agent didn't stay in the command: see its conversation)")
        if (reason := ctx.refused(session)) is not None:
            raise CheckFailed(f"session {session_id}: {reason}")
        if session["status"] == "working":
            calls = bash_results(ctx.conversation_of(session_id))
            if any(result is None for _, result in calls):
                in_call = True
                break
        time.sleep(1)
    if ctx.session(session_id)["status"] != "working":
        raise CheckFailed(f"session {session_id} didn't start working within {ctx.settings.turn_seconds}s")
    supervisor = read_supervisor(ctx, session_id)
    markers = [session_id, marker]
    # The command's own processes show up a moment after the call does.
    deadline = time.monotonic() + 10
    while True:
        before = session_processes(supervisor, markers)
        if not in_call or any(marker in command for (_, _, command) in before.values()) or time.monotonic() >= deadline:
            break
        time.sleep(0.5)
    # The leftover check below is only as good as this: it must see the running agent.
    agent = supervisor.get("agentPid")
    if agent not in before:
        raise CheckFailed(f"session {session_id}'s agent (pid {agent}) isn't among its processes in /proc: "
                          f"the leftover check would see nothing")
    agent_uid = before[agent][1]
    expected_uid = pwd.getpwnam(ctx.settings.agent_user).pw_uid
    if agent_uid != expected_uid:
        raise CheckFailed(f"session {session_id}'s agent runs as uid {agent_uid}, not {ctx.settings.agent_user} ({expected_uid})")
    ctx.api.call("DELETE", f"/api/sessions/{session_id}", expect=(409,))
    killed = ctx.api.call("POST", f"/api/sessions/{session_id}/kill",
                          {"caller": CALLER, "reason": "The beta check kills a working session."})
    got = (killed.get("status"), killed.get("killSource"), killed.get("killCaller"))
    if got != ("killed", "user", CALLER):
        raise CheckFailed(f"the kill answered status/source/caller {got}, expected ('killed', 'user', {CALLER!r})")
    if ctx.session(session_id)["status"] != "killed":
        raise CheckFailed(f"session {session_id} isn't killed after its kill")
    deadline = time.monotonic() + ctx.settings.kill_seconds
    while True:
        now = session_processes(supervisor, markers)
        # Still there: the same process as before (pid and start time), or a new one of the session's.
        left = {pid: p for pid, p in now.items() if pid not in before or before[pid][0] == p[0]}
        if not left:
            break
        if time.monotonic() >= deadline:
            raise CheckFailed(f"{len(left)} processes of killed session {session_id} still run after {ctx.settings.kill_seconds}s: {describe(left)}")
        time.sleep(0.5)
    what = "in its Bash call" if in_call else "before it called a tool"
    command_seen = sum(marker in command for (_, _, command) in before.values())
    return (f"session {session_id} (agent as {ctx.settings.agent_user}) refused deletion while working, then killed {what}; "
            f"its {len(before)} processes ({command_seen} of them the command's) are gone")


def check_delete(ctx):
    session_id = ctx.kill_session
    ctx.api.call("DELETE", f"/api/sessions/{session_id}", expect=(204,))
    ctx.api.call("GET", f"/api/sessions/{session_id}", expect=(404,))
    ctx.api.call("DELETE", f"/api/sessions/{session_id}", expect=(404,))
    return f"killed session {session_id} deleted (then 404)"


def check_events(ctx):
    expected = {
        ctx.tool_session: [
            ("session.created", {}), ("session.started", {}), ("session.completed", {}),
            ("session.queued", {"previous": "completed"}), ("session.started", {}), ("session.completed", {}),
        ],
        ctx.kill_session: [
            ("session.created", {}), ("session.started", {}),
            ("session.killed", {"source": "user", "caller": CALLER}),
        ],
    }
    deadline = time.monotonic() + 10
    while True:
        problems = {sid: event_problems(ctx.events.of(sid), wanted) for sid, wanted in expected.items()}
        problems = {sid: p for sid, p in problems.items() if p}
        if not problems:
            break
        if time.monotonic() >= deadline:
            broken = f" ({ctx.events.error})" if ctx.events.error else ""
            raise CheckFailed("; ".join(f"session {sid}: {'; '.join(p)}" for sid, p in problems.items()) + broken)
        time.sleep(0.5)
    return f"the stream delivered both sessions' lifecycles ({sum(len(ctx.events.of(s)) for s in expected)} events)"


def check_record(ctx):
    ctx.api.call("PUT", f"/api/env/{PASSED_VARIABLE}", {"value": ctx.settings.version, "default": False})
    return f"{PASSED_VARIABLE}={ctx.settings.version} (a retried deploy of it starts no sessions)"


class Check:
    def __init__(self, name, run, turns=0, sessions=False):
        self.name, self.run, self.turns, self.sessions = name, run, turns, sessions


# In order: each builds on the ones before it. `turns` is what a check costs in model turns.
CHECKS = [
    Check("health", check_health),
    Check("server info", check_server_info),
    Check("auth", check_auth),
    Check("provider health", check_provider_health),
    Check("earlier runs", check_earlier_runs),
    Check("event stream", open_events, sessions=True),
    Check("tool session", check_tool_session, turns=1, sessions=True),
    Check("conversation", check_conversation, sessions=True),
    Check("activity", check_activity, sessions=True),
    Check("continue", check_continue, turns=1, sessions=True),
    Check("kill", check_kill, turns=1, sessions=True),
    Check("delete", check_delete, sessions=True),
    # Next: the repository checkout (a session that clones a repo into its workplace) goes here,
    # with its own session and turn; add its lifecycle to check_events.
    Check("lifecycle events", check_events, sessions=True),
    Check("record", check_record, sessions=True),
]


def cleanup(ctx):
    """Whatever happened: no default variable left registered, none of this run's sessions running."""
    problems = []
    if ctx.nonce_registered:
        try:
            ctx.api.call("DELETE", f"/api/env/{NONCE_VARIABLE}", expect=(204, 404))
        except CheckFailed as error:
            problems.append(f"could not remove {NONCE_VARIABLE}: {error}")
    for session_id in ctx.created:
        try:
            status, session = http("GET", f"{ctx.api.base}/api/sessions/{session_id}", headers=ctx.api.auth)
            if status == 200 and session["status"] not in ENDED:
                ctx.api.call("POST", f"/api/sessions/{session_id}/kill",
                             {"caller": CALLER, "reason": "The beta check stopped."}, expect=(200, 409))
                print(f"cleanup: killed session {session_id}", flush=True)
        except CheckFailed as error:
            problems.append(f"could not stop session {session_id}: {error}")
    return problems


def run(ctx, checks):
    failed = None
    try:
        for index, check in enumerate(checks):
            if check.sessions and ctx.already_passed:
                continue
            try:
                print(f"ok   {check.name}: {check.run(ctx)}", flush=True)
            except CheckFailed as error:
                failed = (check, str(error))
            except Exception as error:  # a bug here, or ARM answering something unexpected
                failed = (check, f"unexpected {type(error).__name__}: {error}")
            if failed:
                print(f"FAIL {check.name}: {failed[1]}", flush=True)
                skipped = [c.name for c in checks[index + 1:] if not (c.sessions and ctx.already_passed)]
                if skipped:
                    print(f"skipped (they build on it): {', '.join(skipped)}", flush=True)
                break
    finally:
        problems = cleanup(ctx)
    for problem in problems:
        print(f"FAIL cleanup: {problem}", flush=True)
    if failed or problems:
        print("Beta check FAILED", flush=True)
        return 1
    turns = 0 if ctx.already_passed else sum(c.turns for c in checks)
    print(f"Beta check passed for {ctx.settings.version} ({turns} model turns)", flush=True)
    return 0


def main():
    # A TERM (the step's timeout) still cleans up.
    signal.signal(signal.SIGTERM, lambda *_: sys.exit("Beta check FAILED: stopped (timeout)"))
    try:
        settings = Settings({**os.environ, **globals().get("PRESET", {})})
        token = settings.token or client_credentials_token(settings)
    except CheckFailed as error:
        print(f"FAIL setup: {error}\nBeta check FAILED", flush=True)
        return 1
    return run(Context(settings, Api(settings.api, token)), CHECKS)


if __name__ == "__main__":
    sys.exit(main())
