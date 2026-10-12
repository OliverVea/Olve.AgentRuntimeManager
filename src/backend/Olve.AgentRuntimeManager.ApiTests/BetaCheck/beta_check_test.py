"""The beta check's own logic (src/deploy/vm/beta-check.py), on what a working ARM never answers:
each way a conversation, an activity or a session's events can be wrong is caught, and said.
Run by BetaCheckTests.CheckLogic_PassesItsOwnTests (python3 beta_check_test.py)."""

import base64
import contextlib
import http.server
import importlib.util
import io
import json
import os
import signal
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("beta_check", os.path.join(HERE, "beta-check.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)

PROMPT = "the prompt"
LINE = "user=arm-agent nonce=abc version=1.2.3 withheld="


def conversation():
    entries = [
        {"seq": 1, "kind": "prompt", "text": PROMPT},
        {"seq": 2, "kind": "thinking", "text": "hm"},
        {"seq": 3, "kind": "tool_call", "tool": "Bash", "toolId": "t1", "input": {"command": "echo"}},
        {"seq": 4, "kind": "tool_result", "toolId": "t1", "text": LINE, "isError": False},
        {"seq": 5, "kind": "text", "text": "It printed a line."},
        {"seq": 6, "kind": "turn_end", "text": "It printed a line.", "isError": False},
    ]
    return {"entries": entries, "turns": 1, "messages": 1, "toolCalls": 1, "lastSeq": 6}


def activity():
    return [{"kind": "tool", "text": "Bash → Print who I am"}, {"kind": "text", "text": "It printed a line."}]


class ConversationTests(unittest.TestCase):
    def problems(self, change):
        c = conversation()
        change(c)
        return "; ".join(check.conversation_problems(c, [PROMPT]))

    def test_a_good_one_has_none(self):
        self.assertEqual(check.conversation_problems(conversation(), [PROMPT]), [])

    def test_each_flaw_is_said(self):
        def drop(seq):
            return lambda c: c["entries"].pop(seq - 1)

        cases = {
            "seqs aren't 1..": lambda c: c["entries"][2].update(seq=9),
            "lastSeq is 5": lambda c: c.update(lastSeq=5),
            "unknown kinds: ['image']": lambda c: c["entries"][1].update(kind="image"),
            "its prompts are": lambda c: c["entries"][0].update(text="another"),
            "it starts with thinking": lambda c: c["entries"].insert(0, c["entries"].pop(1)),
            "don't alternate": drop(6),
            "doesn't end with a turn that succeeded": lambda c: c["entries"][5].update(isError=True),
            "turns is 2, but it has 1": lambda c: c.update(turns=2),
            "messages is 0, but it has 1": lambda c: c.update(messages=0),
            "toolCalls is 3, but it has 1": lambda c: c.update(toolCalls=3),
            "no Bash tool call": lambda c: c["entries"][2].update(tool="Read"),
            "the Bash call has no result": lambda c: c["entries"][3].update(toolId="t2"),
            "out of order or failed": lambda c: c["entries"][3].update(isError=True),
            "has no toolId or command": lambda c: c["entries"][2].update(input={}),
            "no text from the agent in its first turn": lambda c: c["entries"][4].update(kind="thinking"),
        }
        for expected, change in cases.items():
            with self.subTest(expected):
                self.assertIn(expected, self.problems(change))

    def test_two_turns_need_both_prompts(self):
        c = conversation()
        self.assertIn("turns is 1, expected 2", "; ".join(check.conversation_problems(c, [PROMPT, "more"])))


class ToolOutputTests(unittest.TestCase):
    def test_reads_the_labelled_line(self):
        self.assertEqual(check.tool_output(conversation()),
                         {"user": "arm-agent", "nonce": "abc", "version": "1.2.3", "withheld": ""})

    def test_says_when_there_is_none(self):
        c = conversation()
        c["entries"][3]["text"] = "command not found"
        with self.assertRaisesRegex(check.CheckFailed, "no Bash result has the command's"):
            check.tool_output(c)
        c["entries"][2]["tool"] = "Read"
        with self.assertRaisesRegex(check.CheckFailed, r"no Bash tool call \(tool calls: \['Read'\]\)"):
            check.tool_output(c)


class ActivityTests(unittest.TestCase):
    def problems(self, lines, c=None):
        return "; ".join(check.activity_problems(lines, c or conversation()))

    def test_a_good_one_has_none(self):
        self.assertEqual(check.activity_problems(activity(), conversation()), [])

    def test_each_flaw_is_said(self):
        long_line = "x" * 199 + "😀"  # 201 UTF-16 code units
        cases = {
            "no lines": [],
            "unknown kind": [activity()[0], {"kind": "thinking", "text": "hm"}, activity()[1]],
            "an empty line": [activity()[0], {"kind": "text", "text": " "}, activity()[1]],
            "line break or control character": [activity()[0], {"kind": "text", "text": "a\nb"}],
            "longer than 200": [activity()[0], {"kind": "text", "text": long_line}],
            "the conversation's [('tool', 'Bash')]": [{"kind": "tool", "text": "Read → x"}, activity()[1]],
            "no 'Bash → …' line": [{"kind": "text", "text": "hello"}],
            "no line of the agent's text": [activity()[0]],
        }
        for expected, lines in cases.items():
            with self.subTest(expected):
                self.assertIn(expected, self.problems(lines))

    def test_a_failed_tool_call_needs_its_error_line(self):
        c = conversation()
        c["entries"][3]["isError"] = True
        self.assertIn("the conversation's [('tool', 'Bash'), ('error', None)]", self.problems(activity(), c))
        lines = [activity()[0], {"kind": "error", "text": "it failed"}, activity()[1]]
        self.assertEqual(check.activity_problems(lines, c), [])


class EventTests(unittest.TestCase):
    EXPECTED = [("session.created", {}), ("session.started", {}), ("session.killed", {"source": "user"})]

    def events(self):
        return [{"type": t, "at": "2026-10-12T00:00:00Z", "sessionId": "s", **f}
                for t, f in [("session.created", {}), ("session.queued", {}), ("session.started", {}),
                             ("session.killed", {"source": "user"})]]

    def test_in_order_with_others_between_is_fine(self):
        self.assertEqual(check.event_problems(self.events(), self.EXPECTED), [])

    def test_each_flaw_is_said(self):
        missing, swapped, bare, other = self.events(), self.events(), self.events(), self.events()
        missing.pop(3)
        swapped[0], swapped[2] = swapped[2], swapped[0]
        del bare[1]["at"]
        other[3]["source"] = "timeout"
        self.assertIn("no session.killed {'source': 'user'}", "; ".join(check.event_problems(missing, self.EXPECTED)))
        self.assertIn("no session.started where expected", "; ".join(check.event_problems(swapped, self.EXPECTED)))
        self.assertIn("an event without at", "; ".join(check.event_problems(bare, self.EXPECTED)))
        self.assertIn("no session.killed", "; ".join(check.event_problems(other, self.EXPECTED)))


class Authority(http.server.BaseHTTPRequestHandler):
    """An OIDC Authority's discovery document and token endpoint, for one confidential client."""

    def do_GET(self):
        self.answer(200, {"token_endpoint": f"http://127.0.0.1:{self.server.server_port}/token"})

    def do_POST(self):
        form = urllib.parse.parse_qs(self.rfile.read(int(self.headers["Content-Length"])).decode())
        good = form == {"grant_type": ["client_credentials"], "client_id": ["olve-arm"],
                        "client_secret": ["s3cret+/="], "scope": ["openid"]}
        self.answer(200, {"access_token": "the-token"}) if good else self.answer(401, {"error": "invalid_client", "got": form})

    def answer(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


class SettingsTests(unittest.TestCase):
    def settings(self, port, secret):
        env_file = tempfile.NamedTemporaryFile("w", suffix=".env", delete=False)
        with env_file:
            env_file.write(f"Port=5000\nAuth__Authority=http://127.0.0.1:{port}/application/o/olve-arm/\n"
                           "Auth__Audience=olve-arm\nProviders__Claude__WorkRoot=/sessions\nSupervisor__User=arm-agent\n")
        self.addCleanup(os.unlink, env_file.name)
        # As deploy-beta passes them: a PRESET over the environment.
        return check.Settings({"BETA_CHECK_VERSION": "from-env", "BETA_CHECK_ENV_FILE": env_file.name,
                               **{"BETA_CHECK_VERSION": "1.2.3",
                                  "BETA_CHECK_CLIENT_SECRET_B64": base64.b64encode(secret.encode()).decode()}})

    def test_a_client_credentials_token_from_the_authority(self):
        server = http.server.HTTPServer(("127.0.0.1", 0), Authority)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.shutdown)

        settings = self.settings(server.server_port, "s3cret+/=")
        self.assertEqual((settings.version, settings.agent_user, settings.api), ("1.2.3", "arm-agent", "http://localhost:5000"))
        self.assertEqual(check.client_credentials_token(settings), "the-token")

        # A refused secret fails with the status only: the answer (which may echo it) isn't shown.
        with self.assertRaises(check.CheckFailed) as refused:
            check.client_credentials_token(self.settings(server.server_port, "wrong-secret"))
        self.assertEqual(str(refused.exception), "the Authority refused the client-credentials token (401)")

    def test_says_what_is_missing(self):
        with self.assertRaisesRegex(check.CheckFailed, "BETA_CHECK_VERSION is not set"):
            check.Settings({})
        with self.assertRaisesRegex(check.CheckFailed, "can't read ARM's config /no/such/env"):
            check.Settings({"BETA_CHECK_VERSION": "1", "BETA_CHECK_ENV_FILE": "/no/such/env"})


class ProcessTests(unittest.TestCase):
    def test_finds_a_session_s_processes_and_their_children_until_they_are_gone(self):
        marker = f"beta-check-test-{os.getpid()}"
        # A process that names the marker, with a child that doesn't.
        process = subprocess.Popen(["sh", "-c", "sleep 30; echo", marker], start_new_session=True)
        try:
            deadline = time.monotonic() + 5
            while True:
                found = check.session_processes({}, [marker])
                if len(found) >= 2 or time.monotonic() > deadline:
                    break
                time.sleep(0.05)
            self.assertIn(process.pid, found)
            self.assertTrue(any(command.startswith("sleep 30") for (*_, command) in found.values()), found)
            self.assertEqual(check.session_processes({"supervisorPid": process.pid}, []).keys(), found.keys())
        finally:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        deadline = time.monotonic() + 5
        while check.session_processes({}, [marker]) and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertEqual(check.session_processes({}, [marker]), {})


    def test_after_a_kill_finds_what_left_the_tree_in_a_session_it_led(self):
        marker = f"beta-check-test-{os.getpid()}-led"
        # A session leader naming the marker, and a grandchild naming nothing that left its tree at once.
        process = subprocess.Popen(["sh", "-c", "sh -c 'sleep 31 & echo $!' & wait; sleep 30", marker],
                                   start_new_session=True, stdout=subprocess.PIPE, text=True)
        orphan = int(process.stdout.readline())
        try:
            before = check.session_processes({}, [marker])
            self.assertIn(process.pid, before)
            os.kill(process.pid, signal.SIGKILL)
            process.wait()
            # The leader is gone; the orphan isn't found as before, but is in the session it led.
            self.assertNotIn(orphan, check.session_processes({}, [marker]))
            self.assertIn(orphan, check.leftovers(before, {}, [marker]))
        finally:
            os.killpg(process.pid, signal.SIGKILL)


if __name__ == "__main__":
    unittest.main()
