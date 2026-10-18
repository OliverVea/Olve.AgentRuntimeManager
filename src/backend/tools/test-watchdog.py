#!/usr/bin/env python3
"""Runs `dotnet test` (the command after `--`) and fails it, instead of letting it wait for ever,
when a test app has died but its output can't end.

`dotnet test` reports a test app only once its output pipe is closed by every process holding it.
A test host that dies (killed, crashed) leaves whatever it started holding that pipe: an orphan
that outlives it (a process started without its own stdout), or, when the HangDump controller
between `dotnet test` and the host is what died, the host itself, which then runs on unwatched
(its hang dump and `--timeout` lived in the controller). `dotnet test` then waits as long as they
run, which can be for ever.

So: each test app `dotnet test` starts (its direct children) is noted with the pipe it writes to.
Once one has exited, if, GRACE seconds later, others still hold that pipe while `dotnet test`
runs, the run fails here: the dead test app is named, with the processes holding its output, and
`dotnet test` is ended through the process group it was started in (its own, made here). Nothing
else is signalled: the holders are named, not killed (they're found by looking, not proven ours).

Looks only at this user's processes (/proc shows others' descriptors to root only), which is all
the test run is: the gate runs it as an unprivileged user (tools/as-test-user.sh). Where there's
no /proc (macOS), it just runs the command. From a terminal (stdin a tty) `dotnet test` stays in
the terminal's process group, so Ctrl+C and the terminal reach it as before; it's then ended alone.
"""

import os
import signal
import subprocess
import sys
import time

GRACE = float(os.environ.get("TEST_WATCHDOG_GRACE", "60"))
POLL = 1.0


def stat(pid):
    """(parent pid, start time) of a running process (not a zombie); None if there's none."""
    try:
        with open(f"/proc/{pid}/stat") as f:
            text = f.read()
    except OSError:
        return None
    fields = text[text.rindex(")") + 2:].split()
    if fields[0] in ("Z", "X"):
        return None
    return int(fields[1]), int(fields[19])


def command_line(pid):
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            return f.read().replace(b"\0", b" ").decode(errors="replace").strip()
    except OSError:
        return "?"


def pipe_of(pid, fd):
    """The pipe (`pipe:[inode]`) a process's descriptor is, or None."""
    try:
        target = os.readlink(f"/proc/{pid}/fd/{fd}")
    except OSError:
        return None
    return target if target.startswith("pipe:") else None


def pids():
    for entry in os.listdir("/proc"):
        if entry.isdigit():
            yield int(entry)


def holders(pipes, exclude):
    """The processes (but `exclude`) holding any of `pipes` open."""
    found = []
    for pid in pids():
        if pid in exclude:
            continue
        try:
            descriptors = os.listdir(f"/proc/{pid}/fd")
        except OSError:
            continue
        for fd in descriptors:
            try:
                if os.readlink(f"/proc/{pid}/fd/{fd}") in pipes:
                    found.append(pid)
                    break
            except OSError:
                pass
    return found


def main():
    if "--" not in sys.argv:
        print("usage: test-watchdog.py -- <command> [arguments]", file=sys.stderr)
        return 2
    command = sys.argv[sys.argv.index("--") + 1:]
    if not os.path.isdir("/proc/self/fd"):
        os.execvp(command[0], command)
    # A group of its own (to end it with everything it started but the supervisors, which have
    # theirs), unless it's run from a terminal, whose foreground group it must stay in.
    own_group = not os.isatty(0)
    # Our own stdout/stderr: a test app that writes straight to them has no pipe of its own to watch.
    shared = {pipe_of("self", 1), pipe_of("self", 2)} - {None}

    test = subprocess.Popen(command, process_group=0 if own_group else None)
    me = os.getpid()

    def end(signum):
        # Still our child, not reaped (poll() said it runs): its pid, and its group's id, are its own.
        if test.poll() is None:
            if own_group:
                os.killpg(test.pid, signum)
            else:
                test.send_signal(signum)

    def forward(signum, _frame):
        end(signum)

    signal.signal(signal.SIGINT, forward if own_group else signal.SIG_IGN)
    signal.signal(signal.SIGTERM, forward)
    signal.signal(signal.SIGHUP, forward)

    apps = {}  # pid -> {"start", "command", "pipes", "exited"}
    while test.poll() is None:
        for pid in pids():
            if pid in apps:
                continue
            status = stat(pid)
            if status is None or status[0] != test.pid:
                continue
            pipes = {pipe_of(pid, 1), pipe_of(pid, 2)} - {None} - shared
            if pipes:
                apps[pid] = {"start": status[1], "command": command_line(pid), "pipes": pipes, "exited": None}

        now = time.monotonic()
        for pid, app in apps.items():
            status = stat(pid)
            if app["exited"] is None and (status is None or status[1] != app["start"]):
                app["exited"] = now
            if app["exited"] is None or now - app["exited"] < GRACE:
                continue
            held = holders(app["pipes"], {me, test.pid})
            if not held or test.poll() is not None:
                continue
            print(f"\ntest-watchdog: test app {pid} exited {now - app['exited']:.0f} s ago, but dotnet test is still "
                  f"waiting for its output, held open by what it left running:\n  test app: {app['command']}", flush=True)
            for holder in held:
                print(f"  held by {holder}: {command_line(holder)}", flush=True)
            print("test-watchdog: failing the run instead of waiting (dotnet test is killed, with its process group "
                  "if it has its own; the processes above are not).", flush=True)
            end(signal.SIGKILL)
            test.wait()
            return 1
        time.sleep(POLL)

    return test.returncode


if __name__ == "__main__":
    sys.exit(main())
