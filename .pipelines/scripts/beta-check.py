#!/usr/bin/env python3
"""ARM's beta checks: proves a release's main paths end to end with real sessions on beta.

Run by .pipelines/scripts/test-after-beta.sh in the pipeline's own job, after the deploy, against
beta from outside: only over HTTP, through ARM's API (the host relay), like any client, with a
client-credentials token from beta's Authentik. Nothing runs in the VM. Every check either passes,
with one line saying what it proved, or fails the step, saying plainly what failed; the checks after
a failed one are skipped (they build on it). Prod deploys only if every check passes.

One model turn per deploy (Haiku): a `claude` session whose agent runs one small command with its
Bash tool; the command's output, in the conversation, says who it ran as and which variables reached
it. Everything else runs on the `fake` provider (no model; enabled on beta, never on prod): a
conversation and its activity, a message continuing an ended session, a kill, a delete, the event
stream, and the record. Every wait is bounded.

Settings, from the environment:
  BETA_CHECK_API                ARM's base URL (required), e.g. http://100.100.117.17:18792
  BETA_CHECK_VERSION            the bundle's version (required): beta must serve it, and it's the
                                record that a retried run has already been confirmed
  BETA_CHECK_TOKEN              a token to use as is (the local tests), or, for a client-credentials
                                token of ARM's confidential machine client:
  BETA_CHECK_AUTHORITY          the OIDC Authority (its discovery document names the token endpoint)
  BETA_CHECK_CLIENT_ID          the client's id
  BETA_CHECK_CLIENT_SECRET      its secret (from the environment only: never on a command line)
  BETA_CHECK_CA_FILE            a CA (PEM) to trust for the Authority's HTTPS, besides the system's
  BETA_CHECK_ENVIRONMENT        what /api/server-info must say (default beta)
  BETA_CHECK_AGENT_USER         the user agents must run as (default arm-agent)
  BETA_CHECK_NOT_AGENT_USERS    users an agent must never run as (default arm,root: ARM's own, root)
  BETA_CHECK_TURN_SECONDS       how long the model turn may take (default 60)
  BETA_CHECK_PROVIDER / _MODEL  default claude / haiku

Idempotent per bundle: a passing run leaves a record in ARM's history (a completed `fake` session,
"Beta checks passed for <version>"), and a run for a version with a record starts no sessions. A
run first kills what an earlier, failed run left running. What it leaves on beta is ARM's history:
the claude session, the continued fake session and the record (and, on a failure, what failed); the
killed fake session is deleted.

Adding a check: write `check_<what>(ctx) -> str` (return what it proved; raise CheckFailed with
what failed), add it to CHECKS with the model turns it costs (the budget is one: use the `fake`
provider wherever it can prove the point), and test it in the API tests' BetaCheckTests (a local
ARM with a stub Claude Code) and, for what a working ARM never answers, beta_check_test.py.
"""

import json
import os
import re
import secrets
import signal
import ssl
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

CALLER = "test-after-beta"
# Marks a session as this check's (its own env, returned with it): which version started it.
TAG = "BETA_CHECK_VERSION"
RUN_TAG = "BETA_CHECK_RUN"
# Registered as a default variable only while the claude session is created (sessions take the
# registered values when they're created), so it reaches that agent and, at most, a session
# someone else creates in that moment.
NONCE_VARIABLE = "BETA_CHECK_NONCE"
# The record of a passing run (a `fake` session's prompt, with the version).
RECORD_PROMPT = "Beta checks passed for {version}: the beta check's record. fake:sleep=100ms"

# The claude session's command. Its output, not the agent's words, decides: who it ran as, and
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

# The fake sessions' scripts (FakeScript, FakeTurn): what their conversations show, and how long they run.
FAKE_PROMPT = ("The beta check's conversation. fake:sleep=1s fake:think=Checking fake:say=Running_a_command "
               "fake:tool=Bash fake:say=It_ran.")
FAKE_CONTINUE = "The beta check continues it. fake:sleep=1s fake:say=Continued."
FAKE_KILL_PROMPT = "A session the beta check kills, then deletes. fake:hang"

ACTIVITY_KINDS = {"text", "tool", "error"}
ACTIVITY_MAX_LENGTH = 200
CONVERSATION_KINDS = {"prompt", "text", "thinking", "tool_call", "tool_result", "turn_end", "notice"}
ENDED = {"completed", "cancelled", "killed", "failed"}


class CheckFailed(Exception):
    """What failed, in words for the pipeline's log."""


class Stopped(BaseException):
    """No one reads the output any more (the step went away): stop, and clean up."""


class TimeUp(BaseException):
    """The run's time is up (the step's `timeout` sent TERM): stop, say where, and clean up."""


_output_gone = False


def say(text, stop=True):
    """Prints a line for the step's log; once that's gone, stops the run (or, in cleanup, goes on silently)."""
    global _output_gone
    if _output_gone:
        return
    try:
        print(text, flush=True)
    except OSError:
        _output_gone = True
        if stop:
            raise Stopped()


def short(value, length=200):
    text = value if isinstance(value, str) else json.dumps(value)
    text = " ".join(text.split())
    return text if len(text) <= length else text[: length - 1] + "…"


# --- settings ---------------------------------------------------------------------------------


class Settings:
    def __init__(self, source):
        def get(name, default=None):
            value = source.get(name)
            return value if value not in (None, "") else default

        def need(name):
            value = get(name)
            if value is None:
                raise CheckFailed(f"{name} is not set")
            return value

        self.version = need("BETA_CHECK_VERSION")
        self.api = need("BETA_CHECK_API").rstrip("/")
        self.environment = get("BETA_CHECK_ENVIRONMENT", "beta")
        self.provider = get("BETA_CHECK_PROVIDER", "claude")
        self.model = get("BETA_CHECK_MODEL", "haiku")
        try:
            self.turn_seconds = int(get("BETA_CHECK_TURN_SECONDS", "60"))
        except ValueError:
            raise CheckFailed("BETA_CHECK_TURN_SECONDS must be whole seconds")
        self.agent_user = get("BETA_CHECK_AGENT_USER", "arm-agent")
        # Set but empty: none (the local tests, where ARM, its agents and the check share a user).
        self.not_agent_users = [u for u in source.get("BETA_CHECK_NOT_AGENT_USERS", "arm,root").split(",") if u]
        if self.agent_user in self.not_agent_users:
            raise CheckFailed(f"BETA_CHECK_AGENT_USER {self.agent_user!r} is one agents must never run as")
        self.token = get("BETA_CHECK_TOKEN")
        self.authority = get("BETA_CHECK_AUTHORITY")
        self.client_id = get("BETA_CHECK_CLIENT_ID")
        self.client_secret = get("BETA_CHECK_CLIENT_SECRET")
        self.ca_file = get("BETA_CHECK_CA_FILE")
        if not self.token:
            missing = [name for name, value in (("BETA_CHECK_AUTHORITY", self.authority), ("BETA_CHECK_CLIENT_ID", self.client_id),
                                                ("BETA_CHECK_CLIENT_SECRET", self.client_secret)) if not value]
            if missing:
                raise CheckFailed(f"no BETA_CHECK_TOKEN, and {', '.join(missing)} not set: no way to get a token")


# --- HTTP -------------------------------------------------------------------------------------


def opener(ca_file=None):
    """Straight to the target (never through a proxy from the environment), trusting `ca_file` too."""
    context = ssl.create_default_context()
    if ca_file:
        try:
            context.load_verify_locations(cafile=ca_file)
        except (OSError, ssl.SSLError) as error:
            raise CheckFailed(f"can't use the CA {ca_file}: {error}")
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))


OPENER = opener()


# How long one request may take; cleanup shortens it, to finish within the step's grace after a TERM.
HTTP_TIMEOUT = 30


def http(method, url, body=None, headers=None, form=False, timeout=None, via=None):
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
        with (via or OPENER).open(request, timeout=timeout or HTTP_TIMEOUT) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    except (urllib.error.URLError, OSError) as error:
        parts = urllib.parse.urlsplit(url)
        raise CheckFailed(f"{method} {parts.scheme}://{parts.netloc}{parts.path}: {getattr(error, 'reason', error)}")
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
    """A token of ARM's machine client from the Authority; what fails says which part, never the secret."""
    via = opener(settings.ca_file)
    authority = settings.authority.rstrip("/") + "/"
    try:
        status, discovery = http("GET", authority + ".well-known/openid-configuration", via=via)
    except CheckFailed as error:
        raise CheckFailed(f"can't reach the Authority ({error}): this job needs a route to it, and its CA "
                          f"(BETA_CHECK_CA_FILE) for HTTPS")
    if status != 200 or not isinstance(discovery, dict) or "token_endpoint" not in discovery:
        raise CheckFailed(f"the Authority's discovery document answered {status}")
    status, token = http("POST", discovery["token_endpoint"], {
        "grant_type": "client_credentials", "client_id": settings.client_id,
        "client_secret": settings.client_secret, "scope": "openid",
    }, form=True, via=via)
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

    def start(self, seconds=10):
        threading.Thread(target=self._read, daemon=True).start()
        if not self._open.wait(seconds) or self.error:
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
            # (A heartbeat opens it; an error, or the stream ending, sets `_open` too, below.)
            data = []
            for raw in response:
                line = raw.decode("utf-8", "replace").rstrip("\r\n")
                if line.startswith("data:"):
                    data.append(line[5:].removeprefix(" "))
                elif line == "" and data:
                    self._dispatch("\n".join(data))
                    data = []
            self.error = "the server ended the event stream"
        except Exception as error:  # the thread's end is reported by the checks that wait on it
            self.error = self.error or f"the event stream broke: {error}"
        finally:
            self._open.set()  # whoever waits for it to open learns now that it didn't

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


# --- what the checks share --------------------------------------------------------------------


class Context:
    def __init__(self, settings, api):
        self.settings = settings
        self.api = api
        self.events = None
        self.already_passed = False
        # This run's: its sessions carry it too (RUN_TAG), so cleanup finds even one whose create answer was lost.
        self.nonce = secrets.token_hex(8)
        self.nonce_registered = False
        self.created = []
        self.claude_session = None
        self.claude_prompt = TOOL_PROMPT.format(command=TOOL_COMMAND)
        self.fake_session = None
        self.fake_conversation = None
        self.kill_session = None

    def tags(self):
        return {TAG: self.settings.version, RUN_TAG: self.nonce}

    def create_session(self, prompt, provider, model, timeout_seconds):
        created = self.api.call("POST", "/api/sessions", {
            "prompt": prompt, "provider": provider, "model": model,
            "caller": CALLER, "timeoutSeconds": timeout_seconds, "env": self.tags(),
        }, expect=(201, 202))
        self.created.append(created["id"])
        return created["id"]

    def create_fake_session(self, prompt):
        return self.create_session(prompt, "fake", "fake", 60)

    def session(self, session_id):
        return self.api.call("GET", f"/api/sessions/{session_id}")

    def conversation_of(self, session_id):
        return self.api.call("GET", f"/api/sessions/{session_id}/conversation")

    def refused(self, session, provider):
        """Why the provider refused a queued session (it would retry later), or None."""
        if session["status"] != "queued" or not session.get("error"):
            return None
        health = self.api.call("GET", "/api/providers/health")
        state = next((h for h in health if h.get("provider") == provider), {})
        return f"the {provider} provider refused it ({state.get('status')}): {session['error']}"

    def wait_ended(self, session_id, seconds, provider="fake", done=lambda s: True):
        """The session once it has ended (and `done` holds), polled; fails after `seconds`."""
        deadline = time.monotonic() + seconds
        while True:
            session = self.session(session_id)
            if session["status"] in ENDED and done(session):
                return session
            if (reason := self.refused(session, provider)) is not None:
                raise CheckFailed(f"session {session_id}: {reason}")
            if time.monotonic() >= deadline:
                position = f" (queue position {session.get('queuePosition')})" if session["status"] == "queued" else ""
                raise CheckFailed(f"session {session_id} is still {session['status']}{position} after {seconds}s")
            time.sleep(0.5)

    def wait_working(self, session_id, seconds=15):
        deadline = time.monotonic() + seconds
        while True:
            session = self.session(session_id)
            if session["status"] == "working":
                return session
            if session["status"] in ENDED:
                raise CheckFailed(f"session {session_id} ended {session['status']} before it could be killed: "
                                  f"{session.get('error') or ''}")
            if time.monotonic() >= deadline:
                raise CheckFailed(f"session {session_id} is still {session['status']} after {seconds}s, not working")
            time.sleep(0.5)


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
    ctx.api.call("POST", "/api/sessions/search", {"limit": 1})
    return "the API refuses a request without a valid token (401) and takes this job's token"


def check_earlier_runs(ctx):
    """A retried run: kill what an earlier run left running; start nothing if it already passed."""
    done = []
    page = ctx.api.call("POST", "/api/sessions/search", {"caller": CALLER, "status": ["queued", "working"], "limit": 100})
    for session in page["items"]:
        if TAG in (session.get("env") or {}):
            ctx.api.call("POST", f"/api/sessions/{session['id']}/kill",
                         {"caller": CALLER, "reason": "Left running by an earlier beta check."}, expect=(200, 409))
            done.append(f"killed session {session['id']}")
    variables = {v["name"] for v in ctx.api.call("GET", "/api/env")}
    if NONCE_VARIABLE in variables:
        ctx.api.call("DELETE", f"/api/env/{NONCE_VARIABLE}", expect=(204, 404))
        done.append(f"removed {NONCE_VARIABLE}")
    record = RECORD_PROMPT.format(version=ctx.settings.version)
    page = ctx.api.call("POST", "/api/sessions/search", {"caller": CALLER, "status": ["completed"], "limit": 100})
    passed = next((s for s in page["items"] if s.get("prompt") == record
                   and (s.get("env") or {}).get(TAG) == ctx.settings.version), None)
    if passed is not None:
        ctx.already_passed = True
        done.append(f"{ctx.settings.version} already passed (record {passed['id']}): no sessions this time")
    return "; ".join(done) or "none left over"


def check_provider_health(ctx):
    health = ctx.api.call("GET", "/api/providers/health")
    for name, why in ((ctx.settings.provider, "the model turn's"), ("fake", "every other check's (it's enabled on beta)")):
        provider = next((h for h in health if h.get("provider") == name), None)
        if provider is None:
            raise CheckFailed(f"no {name} provider ({why}) in {[h.get('provider') for h in health]}")
        if provider.get("status") != "available":
            until = f" until {provider['until']}" if provider.get("until") else ""
            raise CheckFailed(f"the {name} provider ({why}) is {provider.get('status')}{until}: {provider.get('reason')}")
    return ", ".join(f"{h.get('provider')} {h.get('status')}" for h in health)


def open_events(ctx):
    ctx.events = Events(ctx.api)
    ctx.events.start()
    return "GET /api/events streams (heartbeat on connect)"


def tool_calls(conversation, tool=None):
    """(call, result) for each top-level tool call (of `tool`), in order; result None when it has none (yet)."""
    entries = conversation.get("entries") or []
    results = {e.get("toolId"): e for e in entries if e.get("kind") == "tool_result"}
    return [(e, results.get(e.get("toolId"))) for e in entries
            if e.get("kind") == "tool_call" and (tool is None or e.get("tool") == tool) and not e.get("parentToolId")]


def tool_output(conversation):
    """The first Bash result's labelled line, parsed; CheckFailed when there's none."""
    calls = tool_calls(conversation, "Bash")
    if not calls:
        tools = [e.get("tool") for e in conversation.get("entries") or [] if e.get("kind") == "tool_call"]
        raise CheckFailed(f"the agent made no Bash tool call (tool calls: {tools or 'none'})")
    for _, result in calls:
        for line in ((result or {}).get("text") or "").splitlines():
            if (match := TOOL_LINE.search(line)) is not None:
                return match.groupdict()
    raise CheckFailed(f"no Bash result has the command's 'user=… nonce=…' line: {short([r and r.get('text') for _, r in calls])}")


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
        # As ARM counts them: every prompt, a subagent's task too.
        "messages": sum(e.get("kind") == "prompt" for e in entries),
        "toolCalls": sum(e.get("kind") == "tool_call" for e in entries),
    }
    for key, count in counts.items():
        if conversation.get(key) != count:
            problems.append(f"{key} is {conversation.get(key)}, but it has {count}")
    if conversation.get("turns") != len(prompts):
        problems.append(f"turns is {conversation.get('turns')}, expected {len(prompts)}")
    calls = tool_calls(conversation)
    if not calls:
        problems.append("no tool call")
    else:
        call, result = calls[0]
        if not call.get("toolId") or not call.get("tool") or "input" not in call:
            problems.append(f"the tool call has no toolId, tool or input: {short(call)}")
        if result is None:
            problems.append("the tool call has no result")
        elif result.get("seq", 0) < call.get("seq", 0) or result.get("isError"):
            problems.append(f"the tool call's result is out of order or failed: {short(result)}")
    first_end = turn_ends[0].get("seq", 0) if turn_ends else 0
    if not any(e.get("kind") == "text" and e.get("text") and e.get("seq", 0) < first_end for e in entries):
        problems.append("no text from the agent in its first turn")
    return problems


def check_conversation(ctx):
    """A fake session's conversation: what its script says, in order, with consistent counts."""
    ctx.fake_session = session_id = ctx.create_fake_session(FAKE_PROMPT)
    session = ctx.wait_ended(session_id, 20)
    if session["status"] != "completed":
        raise CheckFailed(f"fake session {session_id} ended {session['status']}: {session.get('error') or ''}")
    ctx.fake_conversation = c = ctx.conversation_of(session_id)
    problems = conversation_problems(c, [FAKE_PROMPT])
    kinds = [e.get("kind") for e in c.get("entries") or []]
    expected = ["prompt", "thinking", "text", "tool_call", "tool_result", "text", "turn_end"]
    if kinds != expected:
        problems.append(f"its entries are {kinds}, its script's {expected}")
    if problems:
        raise CheckFailed(f"fake session {session_id}'s conversation: " + "; ".join(problems))
    return f"fake session {session_id}: prompt, thinking, text, a Bash call and its result, text, the turn's end ({len(kinds)} entries)"


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
    if not any(line.get("kind") == "tool" and str(line.get("text")).startswith("Bash → ") for line in lines):
        problems.append("no 'Bash → …' line")
    said = any(e.get("kind") == "text" and not e.get("parentToolId") for e in conversation.get("entries") or [])
    if len(lines) < 50 and said and not any(line.get("kind") == "text" for line in lines):
        problems.append("no line of the agent's text")
    return problems


def check_activity(ctx):
    path = f"/api/sessions/{ctx.fake_session}/activity"
    lines = ctx.api.call("GET", path + "?limit=50")["lines"]
    problems = activity_problems(lines, ctx.fake_conversation)
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
        raise CheckFailed(f"fake session {ctx.fake_session}'s activity: " + "; ".join(problems))
    return f"{len(lines)} bounded lines, oldest first, matching the conversation ({', '.join(l['kind'] for l in lines)}); limit honoured"


def check_continue(ctx):
    """(M11) A message to the ended fake session continues it."""
    session_id = ctx.fake_session
    before = ctx.session(session_id)
    sent = ctx.api.call("POST", f"/api/sessions/{session_id}/messages",
                        {"text": FAKE_CONTINUE, "caller": CALLER}, expect=(202,))
    if sent.get("delivery") != "continued":
        raise CheckFailed(f"the message to ended session {session_id} was {sent.get('delivery')!r}, expected 'continued'")
    session = ctx.wait_ended(session_id, 20, done=lambda s: s.get("endedAt") != before.get("endedAt"))
    if session["status"] != "completed":
        raise CheckFailed(f"the continued session {session_id} ended {session['status']}: {session.get('error') or ''}")
    conversation = ctx.conversation_of(session_id)
    problems = conversation_problems(conversation, [FAKE_PROMPT, FAKE_CONTINUE])
    earlier = ctx.fake_conversation["entries"]
    if conversation.get("entries", [])[: len(earlier)] != earlier:
        problems.append("its first turn's entries changed")
    if problems:
        raise CheckFailed(f"the continued session {session_id}'s conversation: " + "; ".join(problems))
    return (f"session {session_id} continued and completed again: {len(earlier)} → "
            f"{len(conversation['entries'])} entries, {conversation['turns']} turns, {conversation['messages']} messages")


def check_kill_and_delete(ctx):
    """A working fake session: deleting it is refused, a kill ends it `killed`, then it's deleted."""
    ctx.kill_session = session_id = ctx.create_fake_session(FAKE_KILL_PROMPT)
    ctx.wait_working(session_id)
    ctx.api.call("DELETE", f"/api/sessions/{session_id}", expect=(409,))
    killed = ctx.api.call("POST", f"/api/sessions/{session_id}/kill",
                          {"caller": CALLER, "reason": "The beta check kills a working session."})
    got = (killed.get("status"), killed.get("killSource"), killed.get("killCaller"))
    if got != ("killed", "user", CALLER):
        raise CheckFailed(f"the kill answered status/source/caller {got}, expected ('killed', 'user', {CALLER!r})")
    if ctx.session(session_id)["status"] != "killed":
        raise CheckFailed(f"session {session_id} isn't killed after its kill")
    ctx.api.call("POST", f"/api/sessions/{session_id}/kill", {"caller": CALLER}, expect=(409,))
    ctx.api.call("DELETE", f"/api/sessions/{session_id}", expect=(204,))
    ctx.api.call("GET", f"/api/sessions/{session_id}", expect=(404,))
    ctx.api.call("DELETE", f"/api/sessions/{session_id}", expect=(404,))
    return (f"fake session {session_id}: deleting it while it worked was refused (409), the kill ended it killed "
            f"(by the user, {CALLER}), a second kill 409, then deleted (204) and gone (404)")


def check_claude_session(ctx):
    """The one model turn: the agent runs a command; its output says who it ran as and what reached it."""
    settings = ctx.settings
    # Before the PUT: if its answer is lost, it may still have been registered.
    ctx.nonce_registered = True
    ctx.api.call("PUT", f"/api/env/{NONCE_VARIABLE}", {"value": ctx.nonce, "default": True})
    try:
        ctx.claude_session = ctx.create_session(ctx.claude_prompt, settings.provider, settings.model, settings.turn_seconds + 60)
    finally:
        ctx.api.call("DELETE", f"/api/env/{NONCE_VARIABLE}", expect=(204, 404))
        ctx.nonce_registered = False
    session_id = ctx.claude_session
    session = ctx.wait_ended(session_id, settings.turn_seconds, provider=settings.provider)
    if session["status"] != "completed":
        raise CheckFailed(f"session {session_id} ended {session['status']}: {session.get('error') or session.get('killReason') or ''}")
    conversation = ctx.conversation_of(session_id)
    seen = tool_output(conversation)
    if seen["user"] in settings.not_agent_users:
        raise CheckFailed(f"the agent ran as {seen['user']!r}, a user agents must never run as")
    if seen["user"] != settings.agent_user:
        raise CheckFailed(f"the agent ran as {seen['user']!r}, expected {settings.agent_user!r}")
    if seen["nonce"] != ctx.nonce:
        raise CheckFailed(f"the registered default variable {NONCE_VARIABLE} didn't reach the agent (it saw {seen['nonce']!r})")
    if seen["version"] != settings.version:
        raise CheckFailed(f"the session's own variable {TAG} didn't reach the agent (it saw {seen['version']!r})")
    if seen["withheld"].strip():
        raise CheckFailed("ARM's own configuration (Auth__Authority) reached the agent's environment")
    entries = conversation.get("entries") or []
    if not entries or entries[-1].get("kind") != "turn_end" or entries[-1].get("isError"):
        raise CheckFailed(f"session {session_id}'s conversation doesn't end with a turn that succeeded")
    return (f"session {session_id} ({settings.provider}/{settings.model}) completed one turn: its Bash call ran as "
            f"{seen['user']}, the registered default and the session's own variables reached it, ARM's config didn't")


def check_events(ctx):
    expected = {
        ctx.claude_session: [("session.created", {}), ("session.started", {}), ("session.completed", {})],
        ctx.fake_session: [
            ("session.created", {}), ("session.started", {}), ("session.completed", {}),
            ("session.queued", {"previous": "completed"}), ("session.started", {}), ("session.completed", {}),
        ],
        ctx.kill_session: [
            ("session.created", {}), ("session.started", {}),
            ("session.killed", {"source": "user", "caller": CALLER}),
        ],
    }
    deadline = time.monotonic() + 5
    while True:
        problems = {sid: event_problems(ctx.events.of(sid), wanted) for sid, wanted in expected.items()}
        problems = {sid: p for sid, p in problems.items() if p}
        if not problems:
            break
        if time.monotonic() >= deadline:
            broken = f" ({ctx.events.error})" if ctx.events.error else ""
            raise CheckFailed("; ".join(f"session {sid}: {'; '.join(p)}" for sid, p in problems.items()) + broken)
        time.sleep(0.5)
    return f"the stream delivered the three sessions' lifecycles ({sum(len(ctx.events.of(s)) for s in expected)} events)"


def still_serving(ctx, record=None):
    """
    Fails if beta no longer serves the checked version: a newer bundle's deploy may land while the
    checks run (deploy-beta doesn't wait for this step). A record written meanwhile is deleted.
    """
    info = ctx.api.call("GET", "/api/server-info")
    if info.get("version") != ctx.settings.version:
        if record is not None:
            ctx.api.call("DELETE", f"/api/sessions/{record}", expect=(204, 404, 409))
        raise CheckFailed(f"beta now serves {info.get('version')!r}, not {ctx.settings.version!r}: another release was "
                          f"deployed while the checks ran, so they don't vouch for {ctx.settings.version}")


def check_record(ctx):
    still_serving(ctx)
    session_id = ctx.create_fake_session(RECORD_PROMPT.format(version=ctx.settings.version))
    ended = ctx.wait_ended(session_id, 15)
    if ended["status"] != "completed":
        raise CheckFailed(f"the record, fake session {session_id}, ended {ended['status']}: {ended.get('error') or ''}")
    still_serving(ctx, record=session_id)
    return f"session {session_id} records it in ARM's history (a retried run for {ctx.settings.version} starts no sessions)"


class Check:
    def __init__(self, name, run, turns=0, sessions=False):
        self.name, self.run, self.turns, self.sessions = name, run, turns, sessions


# In order: each builds on the ones before it, the free ones before the model turn. `turns` is what
# a check costs in model turns.
CHECKS = [
    Check("health", check_health),
    Check("server info", check_server_info),
    Check("auth", check_auth),
    Check("earlier runs", check_earlier_runs),
    Check("provider health", check_provider_health, sessions=True),
    Check("event stream", open_events, sessions=True),
    Check("conversation", check_conversation, sessions=True),
    Check("activity", check_activity, sessions=True),
    Check("continue", check_continue, sessions=True),
    Check("kill and delete", check_kill_and_delete, sessions=True),
    Check("claude session", check_claude_session, turns=1, sessions=True),
    Check("lifecycle events", check_events, sessions=True),
    Check("record", check_record, sessions=True),
]


def cleanup(ctx):
    """
    Whatever happened: no default variable left registered, none of this run's sessions running
    (those it knows, and any its tag finds). Each step on its own: one failing (or the run's time
    running out in the middle) doesn't skip the others.
    """
    global HTTP_TIMEOUT
    HTTP_TIMEOUT = 3
    problems = []

    def step(what, action):
        try:
            action()
        except (CheckFailed, TimeUp, Stopped) as error:
            problems.append(f"could not {what}: {error or type(error).__name__}")

    def stop(session_id):
        status, session = http("GET", f"{ctx.api.base}/api/sessions/{session_id}", headers=ctx.api.auth)
        if status == 200 and session["status"] not in ENDED:
            ctx.api.call("POST", f"/api/sessions/{session_id}/kill",
                         {"caller": CALLER, "reason": "The beta check stopped."}, expect=(200, 409))
            say(f"cleanup: killed session {session_id}", stop=False)

    def find_ours():
        page = ctx.api.call("POST", "/api/sessions/search", {"caller": CALLER, "status": ["queued", "working"], "limit": 100})
        for session in page["items"]:
            if (session.get("env") or {}).get(RUN_TAG) == ctx.nonce and session["id"] not in ctx.created:
                ctx.created.append(session["id"])

    if ctx.nonce_registered:
        step(f"remove {NONCE_VARIABLE}", lambda: ctx.api.call("DELETE", f"/api/env/{NONCE_VARIABLE}", expect=(204, 404)))
    step("search for this run's sessions", find_ours)
    for session_id in list(ctx.created):
        step(f"stop session {session_id}", lambda: stop(session_id))
    return problems


def run(ctx, checks):
    failed = None
    try:
        for index, check in enumerate(checks):
            if check.sessions and ctx.already_passed:
                continue
            try:
                say(f"ok   {check.name}: {check.run(ctx)}")
            except CheckFailed as error:
                failed = (check, str(error))
            except TimeUp:
                failed = (check, "stopped here: the run's time is up (the step's timeout)")
            except Exception as error:  # a bug here, or ARM answering something unexpected
                failed = (check, f"unexpected {type(error).__name__}: {error}")
            if failed:
                say(f"FAIL {check.name}: {failed[1]}")
                skipped = [c.name for c in checks[index + 1:] if not (c.sessions and ctx.already_passed)]
                if skipped:
                    say(f"skipped (they build on it): {', '.join(skipped)}")
                break
    finally:
        problems = cleanup(ctx)
    for problem in problems:
        say(f"FAIL cleanup: {problem}", stop=False)
    if failed or problems:
        say("Beta check FAILED", stop=False)
        return 1
    turns = 0 if ctx.already_passed else sum(c.turns for c in checks)
    say(f"Beta check passed for {ctx.settings.version} ({turns} model turn{'' if turns == 1 else 's'})", stop=False)
    return 0


def main():
    def time_up(*_):
        signal.signal(signal.SIGTERM, signal.SIG_IGN)  # once: the cleanup that follows isn't cut short
        raise TimeUp()

    # A TERM (the step's timeout) says where it stopped, and still cleans up.
    signal.signal(signal.SIGTERM, time_up)
    try:
        settings = Settings(os.environ)
        token = settings.token or client_credentials_token(settings)
    except (CheckFailed, TimeUp) as error:
        say(f"FAIL setup: {error or 'stopped: the run is out of time'}\nBeta check FAILED", stop=False)
        return 1
    try:
        return run(Context(settings, Api(settings.api, token)), CHECKS)
    except (Stopped, TimeUp):
        return 1


if __name__ == "__main__":
    sys.exit(main())
