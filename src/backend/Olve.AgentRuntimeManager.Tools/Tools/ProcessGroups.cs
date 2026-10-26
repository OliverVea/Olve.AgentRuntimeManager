using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// The processes the calls start. Each <c>arm_bash</c> call runs in a session (and group) of its
/// own, so its command and everything that stays in its group are stopped together; whatever stops
/// the server stops every group (<see cref="KillAll"/>). A process that leaves its group (setsid, a
/// daemon) escapes that: with <paramref name="sweepOrphans"/> (the server is a subreaper,
/// <c>PR_SET_CHILD_SUBREAPER</c>, so such orphans become its children) every process below the
/// server is stopped when the last running call ends, and when the server stops
/// (<see cref="StopOrphansAsync"/>). Only then: once a process has left its group and its parent,
/// nothing tells which call it came from, so a daemon started while calls overlap lives until the
/// last of them ends.
/// </summary>
/// <remarks>
/// Known gap: a server killed with SIGKILL (the OOM killer) stops nothing, and its running calls'
/// groups (and escaped orphans) live on. ARM's own kill of a session walks its process tree,
/// identifying it before it signals, so it catches them while the server still lives; after, they
/// are an orphan kill's to find (by process group), which misses escaped ones. Accepted (2026-10-26).
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class ProcessGroups(bool sweepOrphans = false)
{
    /// <summary>How long a process has after SIGTERM before it gets SIGKILL.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    /// <summary>The running calls' groups (each its leader's pid).</summary>
    private readonly HashSet<int> _groups = [];

    /// <summary>Every call leader ever started: .NET reaps those, so a sweep never does.</summary>
    private readonly HashSet<int> _leaders = [];

    /// <summary>
    /// Starts a call's leader and counts its group as running, at once: a sweep in between would
    /// take it for an orphan.
    /// </summary>
    public Process Start(ProcessStartInfo info)
    {
        lock (_groups)
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException($"'{info.FileName}' did not start.");
            _groups.Add(process.Id);
            _leaders.Add(process.Id);
            return process;
        }
    }

    /// <summary>
    /// A call has ended (its leader exited and was reaped by .NET, which the wait for it waited
    /// for): its group no longer counts as running, and its leader's pid is nothing of ours now.
    /// </summary>
    public void Remove(int group)
    {
        lock (_groups)
        {
            _groups.Remove(group);
            _leaders.Remove(group);
        }
    }

    /// <summary>The call leaders being tracked (for tests: none once every call has ended).</summary>
    public int Leaders
    {
        get
        {
            lock (_groups)
            {
                return _leaders.Count;
            }
        }
    }

    /// <summary>Sends <paramref name="signal"/> to every group running now.</summary>
    public void KillAll(int signal)
    {
        int[] groups;
        lock (_groups)
        {
            groups = [.. _groups];
        }

        foreach (var group in groups)
        {
            Signal(group, signal);
        }
    }

    /// <summary>Whether a process (not a zombie, which has ended) is left in <paramref name="group"/>.</summary>
    public bool IsAlive(int group)
    {
        Reap();
        return group > 1 && Posix.Kill(-group, 0) == 0 && Processes().Any(p => p.Group == group && p.IsRunning);
    }

    /// <summary>
    /// Signals the whole group. Only groups a call made: the id is its leader's pid, which Linux
    /// gives no other process while the group has a member, and a group with none answers ESRCH.
    /// </summary>
    public static void Signal(int group, int signal)
    {
        if (group > 1)
        {
            Posix.SignalOrGone(-group, signal, out _);
        }
    }

    /// <summary>SIGTERM to the group, then SIGKILL to whatever is left of it <see cref="Grace"/> later.</summary>
    public async Task StopAsync(int group)
    {
        Signal(group, Posix.SigTerm);
        var deadline = DateTimeOffset.UtcNow + Grace;
        while (IsAlive(group) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        if (IsAlive(group))
        {
            Signal(group, Posix.SigKill);
        }
    }

    /// <summary>
    /// Stops every process below the server that isn't a running call's (in its group or below its
    /// leader): what calls left behind outside their groups. Only while no call runs, unless
    /// <paramref name="stopping"/> (the server stops: everything goes); either way the ended
    /// children it inherited are reaped. SIGTERM, then SIGKILL after <paramref name="grace"/>
    /// (<see cref="Grace"/> by default), each only while it's the very process found (pid and start time).
    /// </summary>
    public async Task StopOrphansAsync(TimeSpan? grace = null, bool stopping = false)
    {
        if (!sweepOrphans)
        {
            return;
        }

        Reap();
        List<ProcessStatus> orphans;
        lock (_groups)
        {
            if (_groups.Count > 0 && !stopping)
            {
                return;
            }

            orphans = Orphans();
        }

        if (orphans.Count == 0)
        {
            return;
        }

        Signal(orphans, Posix.SigTerm);
        var deadline = DateTimeOffset.UtcNow + (grace ?? Grace);
        while (orphans.Any(Running) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
            Reap();
        }

        Signal(orphans.Where(Running).ToList(), Posix.SigKill);
        await Task.Delay(25);
        Reap();
    }

    private static void Signal(List<ProcessStatus> processes, int signal)
    {
        foreach (var process in processes)
        {
            Posix.SignalIf(process.Pid, signal, status => status.StartTime == process.StartTime);
        }
    }

    private static bool Running(ProcessStatus process) =>
        Posix.Status(process.Pid) is { IsRunning: true } now && now.StartTime == process.StartTime;

    /// <summary>The running processes below the server, but for the running calls' (see <see cref="StopOrphansAsync"/>).</summary>
    private List<ProcessStatus> Orphans()
    {
        var self = Environment.ProcessId;
        var processes = Processes();
        var children = processes.ToLookup(p => p.Parent);
        var found = new List<ProcessStatus>();
        var queue = new Queue<int>([self]);
        while (queue.TryDequeue(out var parent))
        {
            foreach (var child in children[parent])
            {
                // A running call's leader, its group and everything below it are that call's.
                if (_groups.Contains(child.Pid) || _groups.Contains(child.Group))
                {
                    continue;
                }

                if (child.IsRunning)
                {
                    found.Add(child);
                }

                queue.Enqueue(child.Pid);
            }
        }

        return found;
    }

    /// <summary>
    /// Reaps the server's children that have ended but for the calls' leaders (.NET's own): as a
    /// subreaper it inherits orphans, and nothing else waits for them.
    /// </summary>
    private void Reap()
    {
        if (!sweepOrphans)
        {
            return;
        }

        var self = Environment.ProcessId;
        foreach (var process in Processes())
        {
            if (process.Parent == self && !process.IsRunning)
            {
                lock (_groups)
                {
                    if (!_leaders.Contains(process.Pid))
                    {
                        Native.Reap(process.Pid);
                    }
                }
            }
        }
    }

    /// <summary>Every process (not thread) <c>/proc</c> lists now.</summary>
    private static List<ProcessStatus> Processes()
    {
        var found = new List<ProcessStatus>();
        string[] entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch (IOException)
        {
            return found;
        }

        foreach (var entry in entries)
        {
            if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && Posix.Status(pid) is { IsProcess: true } status)
            {
                found.Add(status);
            }
        }

        return found;
    }
}
