"""The beta check's own logic (.pipelines/scripts/beta-check.py), on what a working ARM never
answers: each way a conversation, an activity or a session's events can be wrong is caught, and
said; and how it gets its token. Run by BetaCheckTests.CheckLogic_PassesItsOwnTests (python3 beta_check_test.py)."""

import http.server
import importlib.util
import json
import os
import re
import shutil
import subprocess
import socket
import tempfile
import threading
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
            "no tool call": lambda c: c["entries"][2].update(kind="text"),
            "the tool call has no result": lambda c: c["entries"][3].update(toolId="t2"),
            "out of order or failed": lambda c: c["entries"][3].update(isError=True),
            "has no toolId, tool or input": lambda c: c["entries"][2].pop("input"),
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


class RepoTests(unittest.TestCase):
    URL = "git@github.com:OliverVea/Olve.AgentRuntimeManager.git"

    def session(self, **repo):
        return {"id": "s", "repos": [{"url": self.URL, "folder": "Olve.AgentRuntimeManager", "branch": "arm/1a2b3c4d", **repo}]}

    def test_the_api_s_folder_and_branch(self):
        self.assertEqual(check.api_repo(self.session(), self.URL), ("Olve.AgentRuntimeManager", "arm/1a2b3c4d"))

    def test_an_answer_without_a_folder_or_branch_fails(self):
        cases = {
            "has no folder": self.session(folder=None),
            "has no branch": self.session(branch=" "),
            "has no folder or branch": {"id": "s", "repos": [{"url": self.URL}]},
            "lists the repos [], expected just": {"id": "s"},
            "lists the repos": self.session(url="git@github.com:o/other.git"),
        }
        for expected, session in cases.items():
            with self.subTest(expected), self.assertRaisesRegex(check.CheckFailed, re.escape(expected)):
                check.api_repo(session, self.URL)

    @staticmethod
    def conversation(*lines):
        text = "user=arm-agent nonce=n version=v withheld=\n" + "\n".join(lines)
        return {"entries": [{"seq": 1, "kind": "tool_call", "tool": "Bash", "toolId": "t", "input": {}},
                            {"seq": 2, "kind": "tool_result", "toolId": "t", "text": text}]}

    def problems(self, *lines):
        return "; ".join(check.checkout_problems(self.conversation(*lines), "Olve.AgentRuntimeManager", "arm/1a2b3c4d"))

    def test_a_checkout_as_the_api_says_has_none(self):
        self.assertEqual(self.problems("folder=other path=/w/other toplevel= gitdir= common= branch=",
                                       "folder=Olve.AgentRuntimeManager path=/w/Olve.AgentRuntimeManager "
                                       "toplevel=/w/Olve.AgentRuntimeManager gitdir=/r/x.git/worktrees/O common=/r/x.git "
                                       "branch=arm/1a2b3c4d"), "")

    def test_each_way_it_differs_is_said(self):
        good = ("folder=Olve.AgentRuntimeManager path=/w/Olve.AgentRuntimeManager toplevel=/w/Olve.AgentRuntimeManager "
                "gitdir=/r/x.git/worktrees/O common=/r/x.git branch=")
        cases = {
            "its workplace has no folder 'Olve.AgentRuntimeManager' (the command saw ['x'])":
                ["folder=x path=/w/x toplevel=/w/x gitdir=/r/x.git/worktrees/x common=/r/x.git branch=arm/1a2b3c4d"],
            "isn't a worktree of a shared copy (its git folder is '/w/O/.git', the common one '/w/O/.git')":
                [good.replace("gitdir=/r/x.git/worktrees/O common=/r/x.git", "gitdir=/w/O/.git common=/w/O/.git") + "arm/1a2b3c4d"],
            "its workplace has no folder 'Olve.AgentRuntimeManager' (the command saw none)": [],
            "isn't a git work tree": [good.replace("toplevel=/w/Olve.AgentRuntimeManager", "toplevel=") + "arm/1a2b3c4d"],
            "isn't a checkout of its own: it's inside the work tree /w": [good.replace("toplevel=/w/Olve.AgentRuntimeManager", "toplevel=/w") + "arm/1a2b3c4d"],
            "is on the branch 'main', not the session's 'arm/1a2b3c4d'": [good + "main"],
            "is on the branch '', not": [good],
        }
        for expected, lines in cases.items():
            with self.subTest(expected):
                self.assertIn(expected, self.problems(*lines))

    def test_the_command_reports_a_real_worktree_and_a_plain_folder(self):
        if shutil.which("git") is None:
            self.skipTest("no git")
        git = ["git", "-c", "user.name=t", "-c", "user.email=t@arm.invalid", "-c", "commit.gpgsign=false"]
        with tempfile.TemporaryDirectory() as root:
            origin, workplace = os.path.join(root, "origin"), os.path.join(root, "workplace")
            subprocess.run([*git, "init", "-q", "-b", "main", origin], check=True)
            subprocess.run([*git, "-C", origin, "commit", "-q", "--allow-empty", "-m", "one"], check=True)
            os.mkdir(workplace)
            subprocess.run([*git, "-C", origin, "worktree", "add", "-q", "-b", "arm/1a2b3c4d", os.path.join(workplace, "repo")], check=True)
            os.mkdir(os.path.join(workplace, "plain"))
            subprocess.run([*git, "clone", "-q", "-b", "main", origin, os.path.join(workplace, "clone")], check=True)
            subprocess.run([*git, "-C", os.path.join(workplace, "clone"), "checkout", "-q", "-b", "arm/1a2b3c4d"], check=True)
            ran = subprocess.run(["sh", "-c", check.TOOL_COMMAND], cwd=workplace, capture_output=True, text=True, timeout=30)
            conversation = self.conversation(*ran.stdout.splitlines()[1:])
            self.assertEqual(check.checkout_problems(conversation, "repo", "arm/1a2b3c4d"), [], ran.stdout)
            self.assertIn("isn't a git work tree", "; ".join(check.checkout_problems(conversation, "plain", "arm/1a2b3c4d")))
            self.assertIn("not the session's 'arm/other'", "; ".join(check.checkout_problems(conversation, "repo", "arm/other")))
            # A clone of its own on the right branch isn't one of ARM's checkouts.
            self.assertIn("isn't a worktree of a shared copy", "; ".join(check.checkout_problems(conversation, "clone", "arm/1a2b3c4d")))

    def test_a_url_s_credentials_stay_out_of_the_log(self):
        session = {"id": "s", "repos": [{"url": "https://x-token:s3cret@github.com/o/r.git"}]}
        with self.assertRaises(check.CheckFailed) as failed:
            check.api_repo(session, "https://x-token:s3cret@github.com/o/r.git")
        self.assertNotIn("s3cret", str(failed.exception))
        with self.assertRaises(check.CheckFailed) as failed:
            check.api_repo(session, "https://x-token:s3cret@github.com/o/other.git")
        self.assertNotIn("s3cret", str(failed.exception))


class RecordTests(unittest.TestCase):
    class Api:
        """ARM serving `versions` in turn (the last one from then on), recording what it's asked."""

        def __init__(self, *versions):
            self.calls = []
            self.versions = list(versions)

        def call(self, method, path, body=None, **_):
            self.calls.append((method, path))
            if path == "/api/server-info":
                return {"version": self.versions.pop(0) if len(self.versions) > 1 else self.versions[0]}
            return {"id": "r", "status": "completed"}

    def context(self, api):
        settings = check.Settings({"BETA_CHECK_VERSION": "1.2.3", "BETA_CHECK_API": "http://arm", "BETA_CHECK_TOKEN": "t"})
        return check.Context(settings, api)

    def test_a_release_deployed_before_it_gets_no_record(self):
        api = self.Api("1.2.4")
        with self.assertRaisesRegex(check.CheckFailed, "beta now serves '1.2.4', not '1.2.3': another release was deployed"):
            check.check_record(self.context(api))
        self.assertEqual(api.calls, [("GET", "/api/server-info")])

    def test_a_release_deployed_while_it_is_written_has_it_deleted(self):
        api = self.Api("1.2.3", "1.2.4")
        with self.assertRaisesRegex(check.CheckFailed, "beta now serves '1.2.4'"):
            check.check_record(self.context(api))
        self.assertEqual(api.calls[-1], ("DELETE", "/api/sessions/r"))

    def test_the_same_release_throughout_keeps_it(self):
        api = self.Api("1.2.3")
        self.assertIn("records it", check.check_record(self.context(api)))
        self.assertNotIn("DELETE", [method for method, _ in api.calls])


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
    BASE = {"BETA_CHECK_VERSION": "1.2.3", "BETA_CHECK_API": "http://127.0.0.1:18792/"}

    def settings(self, port, secret, **more):
        # As test-after-beta passes them: in the environment.
        return check.Settings({**self.BASE, "BETA_CHECK_AUTHORITY": f"http://127.0.0.1:{port}/application/o/olve-arm/",
                               "BETA_CHECK_CLIENT_ID": "olve-arm", "BETA_CHECK_CLIENT_SECRET": secret, **more})

    def test_a_client_credentials_token_from_the_authority(self):
        server = http.server.HTTPServer(("127.0.0.1", 0), Authority)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)

        settings = self.settings(server.server_port, "s3cret+/=")
        self.assertEqual((settings.version, settings.api, settings.agent_user, settings.not_agent_users),
                         ("1.2.3", "http://127.0.0.1:18792", "arm-agent", ["arm", "root"]))
        self.assertEqual(check.client_credentials_token(settings), "the-token")

        # A refused secret fails with the status only: the answer (which may echo it) isn't shown.
        with self.assertRaises(check.CheckFailed) as refused:
            check.client_credentials_token(self.settings(server.server_port, "wrong-secret"))
        self.assertEqual(str(refused.exception), "the Authority refused the client-credentials token (401)")

    def test_an_authority_it_cannot_reach_says_so(self):
        with socket.socket() as unused:
            unused.bind(("127.0.0.1", 0))
            port = unused.getsockname()[1]
        with self.assertRaisesRegex(check.CheckFailed, r"can't reach the Authority \(GET http://127.0.0.1:\d+/application/o/olve-arm/"
                                                       r"\.well-known/openid-configuration: .*\): this job needs a route to it"):
            check.client_credentials_token(self.settings(port, "s3cret+/="))

    def test_a_ca_it_cannot_use_says_so(self):
        with tempfile.NamedTemporaryFile("w", suffix=".crt") as ca:
            ca.write("not a certificate")
            ca.flush()
            with self.assertRaisesRegex(check.CheckFailed, f"can't use the CA {ca.name}"):
                check.client_credentials_token(self.settings(1, "s3cret+/=", BETA_CHECK_CA_FILE=ca.name))

    def test_says_what_is_missing(self):
        with self.assertRaisesRegex(check.CheckFailed, "BETA_CHECK_VERSION is not set"):
            check.Settings({})
        with self.assertRaisesRegex(check.CheckFailed, "BETA_CHECK_API is not set"):
            check.Settings({"BETA_CHECK_VERSION": "1"})
        with self.assertRaisesRegex(check.CheckFailed, "no BETA_CHECK_TOKEN, and BETA_CHECK_AUTHORITY, BETA_CHECK_CLIENT_ID, "
                                                       "BETA_CHECK_CLIENT_SECRET not set"):
            check.Settings(self.BASE)
        with self.assertRaisesRegex(check.CheckFailed, "'root' is one agents must never run as"):
            check.Settings({**self.BASE, "BETA_CHECK_TOKEN": "t", "BETA_CHECK_AGENT_USER": "root"})
        self.assertEqual(check.Settings({**self.BASE, "BETA_CHECK_TOKEN": "t", "BETA_CHECK_NOT_AGENT_USERS": ""}).not_agent_users, [])


if __name__ == "__main__":
    unittest.main()
