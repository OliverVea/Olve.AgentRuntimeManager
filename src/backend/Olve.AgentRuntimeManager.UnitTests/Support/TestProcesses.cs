using System.Globalization;
using System.Runtime.CompilerServices;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>
/// The only way tests signal supervisors and agents. The gate runs as root, next to other test
/// processes, with small dense pids (and threads in the same number space: kill(tid) takes the
/// whole process): a pid from a file may name something else by now, and as root nothing stops
/// the signal. So nothing is signalled unless it's proven to be this test host's:
/// <list type="bullet">
/// <item>a supervisor: a running process (not a thread) of this user, a child of this test host
/// (tests start supervisors directly, or through the sudo stub, which execs), running
/// <c>olve-arm-supervisor</c>; its group only if it leads it;</item>
/// <item>an agent: a running process of this user, with its recorded start time, in its
/// supervisor's process group (an id no other group can have while the agent is in it).</item>
/// </list>
/// Every signal, and every one refused, is logged to <see cref="LogPath"/> (the ci task prints it
/// when a test run fails) and to the test's output.
/// </summary>
public static class TestProcesses
{
    /// <summary>Where every signal the tests send is logged: the ci task shows it when a run fails.</summary>
    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "olve-arm-test-kills.log");

    private static readonly Lock LogGate = new();

    /// <summary>Kills every run (supervisor and agent) whose <c>supervisor.json</c> is under <paramref name="root"/>, if proven ours.</summary>
    public static void KillSupervisors(string root, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, SupervisorFiles.Info, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
        {
            if (SupervisorFiles.ReadInfo(Path.GetDirectoryName(path)!) is { } info)
            {
                KillRun(info, caller, file);
            }
        }
    }

    /// <summary>
    /// Kills a run, with one signal, only if proven ours: the group of a supervisor this host started
    /// and that leads it, or else the group its agent is still in (the supervisor gone). The group is
    /// the agent's and the supervisor's alike, so one signal reaches both; a second, to a pid, could
    /// reach a process that took the pid once the first one's target was reaped.
    /// </summary>
    public static void KillRun(SupervisorInfo info, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        if (IsOurSupervisor(info.SupervisorPid, out var why))
        {
            if (Posix.Status(info.SupervisorPid) is { Group: var group } && group == info.SupervisorPid)
            {
                SignalGroup(info.SupervisorPid, "the supervisor's group (it leads it)", caller, file);
            }
            else
            {
                SignalProcess(info.SupervisorPid, "the supervisor (not leading a group)", caller, file);
            }

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
        if (!IsOurSupervisor(pid, out var why))
        {
            Refused(pid, $"supervisor: {why ?? "not running"}", caller, file);
            throw new InvalidOperationException($"Pid {pid} is not a supervisor this test started: {why ?? "not running"}.");
        }

        SignalProcess(pid, "the supervisor", caller, file);
    }

    /// <summary>Whether <paramref name="pid"/> is a running supervisor this test host started; else why not (null: nothing there).</summary>
    private static bool IsOurSupervisor(int pid, out string? why)
    {
        why = null;
        if (pid <= 1 || Posix.Status(pid) is not { IsRunning: true } status)
        {
            return false;
        }

        why = WhyNotOurSupervisor(status);
        return why is null;
    }

    private static string? WhyNotOurSupervisor(ProcessStatus status)
    {
        var command = Posix.CommandLine(status.Pid) ?? "";
        var why = !status.IsProcess ? $"a thread of process {status.ThreadGroup}"
            : status.Uid != Posix.GetEUid() ? $"another user's ({status.Uid})"
            : status.Parent != Environment.ProcessId ? $"not this test host's child (parent {status.Parent})"
            : !command.Contains("olve-arm-supervisor", StringComparison.Ordinal) ? "not a supervisor"
            : null;
        return why is null ? null : $"{why}: {Describe(status)}";
    }

    /// <summary>A process, through a pidfd (<see cref="Posix.SignalIf"/>): checked again right before, and only that process.</summary>
    private static void SignalProcess(int pid, string what, string caller, string file)
    {
        var description = Describe(Posix.Status(pid));
        var result = Posix.SignalIf(pid, Posix.SigKill, status => WhyNotOurSupervisor(status) is null);
        Log($"SIGKILL pid {pid}: {result} ({what}; {description}) from {Path.GetFileNameWithoutExtension(file)}.{caller}");
    }

    /// <summary>A process group whose id was just proven to be the run's (a member checked in it).</summary>
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

    private static void Log(string line)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} [test host {Environment.ProcessId}] {line}";
        Console.WriteLine(entry);
        lock (LogGate)
        {
            try
            {
                File.AppendAllText(LogPath, entry + "\n");
            }
            catch (IOException)
            {
                // Diagnostics only.
            }
        }
    }
}
