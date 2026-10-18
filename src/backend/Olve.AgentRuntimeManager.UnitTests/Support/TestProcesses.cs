using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>
/// The only way tests signal supervisors, agents and their own helper processes. Test hosts run
/// side by side (both test projects at once in the gate), with small dense pids (and threads in the
/// same number space: kill(tid) takes the whole process): a pid from a file may name something else
/// by now. So nothing is found by looking through <c>/proc</c>, and nothing is signalled unless it's
/// proven to be the test's:
/// <list type="bullet">
/// <item>a supervisor: one the test was told was started (<see cref="StartedSupervisors"/>, or ARM's
/// log), still a running process (not a thread) of this user, a child of the process that started it
/// (this test host, or the test's own ARM), running <c>olve-arm-supervisor</c>; its group only if it
/// leads it;</item>
/// <item>an agent: a running process of this user, with its recorded start time, in its
/// supervisor's process group (an id no other group can have while the agent is in it);</item>
/// <item>a helper the test started in a group of its own (<see cref="InOwnGroup"/>): that group,
/// while the helper is still this host's child leading it.</item>
/// </list>
/// Every signal, and every one refused, goes to the test's output (shown when the test fails).
/// </summary>
public static class TestProcesses
{
    /// <summary>
    /// Kills the runs of the <paramref name="supervisors"/> a test started (each the child of
    /// <paramref name="parent"/>, by default this test host), and any agent left by one of them whose
    /// <c>supervisor.json</c> is under <paramref name="root"/>; then waits (bounded) until those
    /// supervisors are gone: one may still be ending (logging, closing its files) just after ARM had
    /// its outcome, and would write into a folder being deleted.
    /// </summary>
    public static void KillSupervisors(string root, IEnumerable<int> supervisors, int? parent = null,
        [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        var started = supervisors.Distinct().ToList();
        foreach (var pid in started)
        {
            KillGroupOf(pid, parent ?? Environment.ProcessId, caller, file);
        }

        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, SupervisorFiles.Info, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
            {
                if (SupervisorFiles.ReadInfo(Path.GetDirectoryName(path)!) is { } info)
                {
                    KillRun(info, parent, caller, file);
                }
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (started.Any(pid => StillThere(pid, parent ?? Environment.ProcessId)) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Kills the process group a supervisor the test started leads (it, its agent and what that
    /// started), if proven ours; a supervisor that doesn't lead one yet (it hasn't detached) alone.
    /// </summary>
    public static void KillGroupOf(int supervisor, int? parent = null, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        if (!IsOurSupervisor(supervisor, parent ?? Environment.ProcessId, out var why))
        {
            if (why is not null)
            {
                Refused(supervisor, $"supervisor: {why}", caller, file);
            }

            return;
        }

        if (Posix.Status(supervisor) is { Group: var group } && group == supervisor)
        {
            SignalGroup(supervisor, "the supervisor's group (it leads it)", caller, file);
        }
        else
        {
            SignalProcess(supervisor, parent ?? Environment.ProcessId, "the supervisor (not leading a group yet)", caller, file);
        }
    }

    /// <summary>
    /// Kills a run, with one signal, only if proven ours: the group of a supervisor started by
    /// <paramref name="parent"/> (this host by default) and that leads it, or else the group its agent
    /// is still in (the supervisor gone). The group is the agent's and the supervisor's alike, so one
    /// signal reaches both; a second, to a pid, could reach a process that took the pid once the first
    /// one's target was reaped.
    /// </summary>
    public static void KillRun(SupervisorInfo info, int? parent = null, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        if (IsOurSupervisor(info.SupervisorPid, parent ?? Environment.ProcessId, out var why))
        {
            KillGroupOf(info.SupervisorPid, parent, caller, file);
            return;
        }

        if (why is not null)
        {
            Refused(info.SupervisorPid, $"supervisor of run {info.RunId}: {why}", caller, file);
        }

        if (Posix.IsInGroup(info.AgentPid, info.SupervisorPid, info.AgentStartTime) && Posix.Status(info.AgentPid) is { } agent && agent.Uid == Posix.GetEUid())
        {
            SignalGroup(info.SupervisorPid, $"the group agent {info.AgentPid} is in (its supervisor gone)", caller, file);
        }
        else if (Posix.Status(info.AgentPid) is { IsRunning: true } other)
        {
            Refused(info.AgentPid, $"agent of run {info.RunId}: not proven the run's ({Describe(other)})", caller, file);
        }
    }

    /// <summary>Kills only a run's supervisor (its agent then is an orphan, as the tests want), if proven ours.</summary>
    public static void KillSupervisor(int pid, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        if (!IsOurSupervisor(pid, Environment.ProcessId, out var why))
        {
            Refused(pid, $"supervisor: {why ?? "not running"}", caller, file);
            throw new InvalidOperationException($"Pid {pid} is not a supervisor this test started: {why ?? "not running"}.");
        }

        SignalProcess(pid, Environment.ProcessId, "the supervisor", caller, file);
    }

    /// <summary>
    /// Kills <paramref name="pid"/>, a process a test's own command said it started (wrote its pid to
    /// a file), if it's still a running process of this user in <paramref name="group"/>: the group of
    /// a supervisor the test started.
    /// </summary>
    public static void KillInGroup(int pid, int group, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        var description = Describe(Posix.Status(pid));
        var result = Posix.SignalIf(pid, Posix.SigKill, status => status.Group == group && status.Uid == Posix.GetEUid());
        Log($"SIGKILL pid {pid}: {result} (a process of supervisor group {group}; {description}) from {Path.GetFileNameWithoutExtension(file)}.{caller}");
    }

    /// <summary>
    /// How to start <paramref name="info"/>'s command in a process group of its own (through
    /// <c>setsid</c>, which execs it: same pid, now leading a new group): it and everything it starts
    /// can then be killed together with <see cref="KillOwnGroup"/>.
    /// </summary>
    public static ProcessStartInfo InOwnGroup(ProcessStartInfo info)
    {
        var command = info.FileName;
        var arguments = info.ArgumentList.ToList();
        info.FileName = "setsid";
        info.ArgumentList.Clear();
        info.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    /// Kills the group <paramref name="process"/> (started with <see cref="InOwnGroup"/>) leads, if
    /// it's still this host's child leading it. A process that has ended has nothing proven left:
    /// nothing is signalled.
    /// </summary>
    public static void KillOwnGroup(Process process, [CallerMemberName] string caller = "", [CallerFilePath] string file = "") =>
        SignalOwnGroup(process, Posix.SigKill, "SIGKILL", caller, file);

    /// <summary>
    /// Stops (SIGSTOP) the group <paramref name="process"/> leads, as <see cref="KillOwnGroup"/>: a
    /// stopped process starts nothing and reaps nothing, so its children stay provably its own.
    /// </summary>
    public static void StopOwnGroup(Process process, [CallerMemberName] string caller = "", [CallerFilePath] string file = "") =>
        SignalOwnGroup(process, SigStop, "SIGSTOP", caller, file);

    private const int SigStop = 19;

    private static void SignalOwnGroup(Process process, int signal, string name, string caller, string file)
    {
        var pid = process.Id;
        if (Posix.Status(pid) is not { IsProcess: true, IsRunning: true } status)
        {
            return;
        }

        if (status.Parent != Environment.ProcessId || status.Group != pid || status.Uid != Posix.GetEUid())
        {
            Refused(pid, $"not this host's child leading its own group ({Describe(status)})", caller, file);
            return;
        }

        Log($"{name} group {pid} (a helper's own group; leader {Describe(status)}) from {Path.GetFileNameWithoutExtension(file)}.{caller}");
        Posix.Kill(-pid, signal);
    }

    /// <summary>
    /// The running processes whose command line (arguments joined by spaces) contains
    /// <paramref name="fragment"/>: for a test to see that something is gone, never to choose what to kill.
    /// </summary>
    public static List<int> WithCommandLine(string fragment)
    {
        var found = new List<int>();
        foreach (var entry in Directory.GetDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(entry), out var pid) && pid != Environment.ProcessId
                && Posix.CommandLine(pid) is { } command && command.Contains(fragment, StringComparison.Ordinal)
                && Posix.StartTime(pid) is not null)
            {
                found.Add(pid);
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="pid"/> is a running supervisor <paramref name="parent"/> started; else why not (null: nothing there).</summary>
    private static bool IsOurSupervisor(int pid, int parent, out string? why)
    {
        why = null;
        if (pid <= 1 || Posix.Status(pid) is not { IsRunning: true } status)
        {
            return false;
        }

        why = WhyNotOurSupervisor(status, parent);
        return why is null;
    }

    private static string? WhyNotOurSupervisor(ProcessStatus status, int parent)
    {
        var command = Posix.CommandLine(status.Pid) ?? "";
        var why = !status.IsProcess ? $"a thread of process {status.ThreadGroup}"
            : status.Uid != Posix.GetEUid() ? $"another user's ({status.Uid})"
            : status.Parent != parent ? $"not a child of {parent} (parent {status.Parent})"
            : !command.Contains("olve-arm-supervisor", StringComparison.Ordinal) ? "not a supervisor"
            : null;
        return why is null ? null : $"{why}: {Describe(status)}";
    }

    /// <summary>Running, or a zombie whose other threads haven't all exited (it may still hold its files); and still <paramref name="parent"/>'s.</summary>
    private static bool StillThere(int pid, int parent)
    {
        if (Posix.Status(pid) is not { IsProcess: true } status || status.Parent != parent)
        {
            return false;
        }

        try
        {
            return status.IsRunning || Directory.GetDirectories($"/proc/{pid}/task").Length > 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A process, through a pidfd (<see cref="Posix.SignalIf"/>): checked again right before, and only that process.</summary>
    private static void SignalProcess(int pid, int parent, string what, string caller, string file)
    {
        var description = Describe(Posix.Status(pid));
        var result = Posix.SignalIf(pid, Posix.SigKill, status => WhyNotOurSupervisor(status, parent) is null);
        Log($"SIGKILL pid {pid}: {result} ({what}; {description}) from {Path.GetFileNameWithoutExtension(file)}.{caller}");
    }

    /// <summary>A process group whose id was just proven to be the test's (its leader, or a member, checked in it).</summary>
    private static void SignalGroup(int group, string what, string caller, string file)
    {
        Log($"SIGKILL group {group} ({what}; leader {Describe(Posix.Status(group))}) from {Path.GetFileNameWithoutExtension(file)}.{caller}");
        Posix.Kill(-group, Posix.SigKill);
    }

    private static void Refused(int pid, string why, string caller, string file) =>
        Log($"NOT signalling pid {pid}, {why}, from {Path.GetFileNameWithoutExtension(file)}.{caller}");

    private static string Describe(ProcessStatus? status) =>
        status is null
            ? "gone"
            : string.Create(CultureInfo.InvariantCulture,
                $"state {status.State}, parent {status.Parent}, group {status.Group}, thread group {status.ThreadGroup}, uid {status.Uid}, started {status.StartTime}, '{Posix.CommandLine(status.Pid)}'");

    /// <summary>Debug output only: the test's output, which shows when the test fails.</summary>
    private static void Log(string line) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} [test host {Environment.ProcessId}] {line}");
}
