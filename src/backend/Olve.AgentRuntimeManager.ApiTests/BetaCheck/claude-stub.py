#!/usr/bin/env python3
"""A stand-in for `claude` for the beta check's tests (BetaCheckTests): speaks Claude Code's
stream-json like the CLI (an init, each message replayed with its uuid, a `result` per turn, done
once its input closes), and runs, for real, the command a message gives in a ```sh block: ARM's arm_bash
tool call, its result and a sentence about it. A message without one gets a one-word answer.

How it misbehaves is chosen by a `stub-mode` file next to it (one word, default `ok`):
  no-env      runs the command without BETA_CHECK_NONCE (a registered variable that went missing)
  leak-env    runs it with Auth__Authority set (ARM's own config reaching the agent)
  no-tool     answers without calling a tool
  hang        takes up the first message and never answers
  wrong-branch  switches each checkout in its workplace to another branch first
  no-checkout   moves each checkout in its workplace aside first (the folder ARM named is missing)
  nested      makes its workplace a git repository and each checkout's folder a plain one in it
  logged-out  `claude auth status` says it isn't logged in
"""

import datetime
import json
import os
import re
import subprocess
import sys
import time
import uuid

HERE = os.path.dirname(os.path.realpath(__file__))
try:
    with open(os.path.join(HERE, "stub-mode")) as mode_file:
        MODE = mode_file.read().strip() or "ok"
except OSError:
    MODE = "ok"
MODEL = "claude-haiku-stub"


def emit(event):
    sys.stdout.write(json.dumps(event) + "\n")
    sys.stdout.flush()


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def assistant(session, *blocks):
    emit({"type": "assistant", "message": {"model": MODEL, "role": "assistant", "content": list(blocks)},
          "parent_tool_use_id": None, "session_id": session, "timestamp": now()})


def tamper():
    """What the misbehaving modes do to the checkouts in the workplace (the current folder) first."""
    folders = [d for d in os.listdir(".") if os.path.isdir(d) and not d.startswith(".")]
    git = ["git", "-c", "user.name=stub", "-c", "user.email=stub@arm.invalid"]
    for folder in folders:
        if MODE == "wrong-branch":
            subprocess.run([*git, "-C", folder, "checkout", "-q", "-b", "beta-check-other"], check=True)
        elif MODE in ("no-checkout", "nested"):
            os.rename(folder, folder + "-moved")
            if MODE == "nested":
                os.mkdir(folder)
    if MODE == "nested":
        subprocess.run([*git, "init", "-q", "."], check=True)


def main(args):
    if args[:2] == ["auth", "status"]:
        logged_in = MODE != "logged-out"
        print(json.dumps({"loggedIn": logged_in, "authMethod": "oauth_token" if logged_in else "none"}))
        return 0 if logged_in else 1
    session = next((args[i + 1] for i, a in enumerate(args[:-1]) if a in ("--session-id", "--resume")), "stub")
    emit({"type": "system", "subtype": "init", "session_id": session, "tools": ["mcp__arm__arm_bash"],
          "mcp_servers": [], "model": MODEL, "claude_code_version": "stub", "skills": [],
          "plugins": [{"name": "agents-md", "path": "builtin", "source": "agents-md@builtin"}]})
    for line in sys.stdin:
        message = json.loads(line)
        text = message["message"]["content"]
        emit({"type": "user", "message": {"role": "user", "content": text}, "session_id": session,
              "parent_tool_use_id": None, "uuid": message.get("uuid"), "timestamp": now(), "isReplay": True})
        if MODE == "hang":
            time.sleep(3600)
        command = re.search(r"```sh\n(.*?)\n```", text, re.S)
        if command is None or MODE == "no-tool":
            answer = "done"
        else:
            command = command.group(1)
            tool_id = "toolu_" + uuid.uuid4().hex
            assistant(session, {"type": "tool_use", "id": tool_id, "name": "mcp__arm__arm_bash",
                                "input": {"command": command, "description": "Run the check's command"}})
            tamper()
            env = dict(os.environ)
            if MODE == "no-env":
                env.pop("BETA_CHECK_NONCE", None)
            if MODE == "leak-env":
                env["Auth__Authority"] = "https://leaked.example/"
            ran = subprocess.run(["bash", "-c", command], env=env, capture_output=True, text=True)
            output = (ran.stdout + ran.stderr).strip()
            emit({"type": "user", "message": {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": tool_id, "content": output, "is_error": ran.returncode != 0}]},
                "parent_tool_use_id": None, "session_id": session, "timestamp": now()})
            answer = f"It printed: {output.splitlines()[0] if output else 'nothing'}"
        assistant(session, {"type": "text", "text": answer})
        emit({"type": "result", "subtype": "success", "is_error": False, "result": answer,
              "session_id": session, "num_turns": 1})
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
