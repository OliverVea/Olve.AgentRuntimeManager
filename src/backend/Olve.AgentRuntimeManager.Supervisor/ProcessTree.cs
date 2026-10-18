using System.Globalization;
using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>A process and everything it started (by parent pid, from <c>/proc</c>), ended gently first.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ProcessTree
{
    /// <summary>
    /// SIGTERM to <paramref name="pid"/> and its descendants (git then removes its own <c>*.lock</c>
    /// files), and SIGKILL to whatever of them is still there <paramref name="grace"/> later. Each is
    /// signalled only while it's the very process found (its pid and start time; through a pidfd): one
    /// that ended meanwhile may have left its pid to another process.
    /// </summary>
    public static async Task StopAsync(int pid, Func<bool> exited, TimeSpan grace)
    {
        var tree = Identify(pid);
        Signal(tree, Posix.SigTerm);

        var deadline = DateTimeOffset.UtcNow + grace;
        while (!exited() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Those it had then and any started meanwhile below the same process, if it's still there.
        var root = tree.FirstOrDefault(p => p.Pid == pid);
        var now = root.StartTime != 0 && Posix.StartTime(pid) == root.StartTime ? Identify(pid) : [];
        Signal(tree.Union(now).ToList(), Posix.SigKill);
    }

    /// <summary>
    /// <paramref name="pid"/> and its descendants that run, each with its start time (a pid and its
    /// start time name one process), read while it still has the parent it was found under: one that
    /// ended meanwhile and left its pid to another process isn't taken for it.
    /// </summary>
    private static List<(int Pid, long StartTime)> Identify(int pid)
    {
        var found = new List<(int Pid, long StartTime)>();
        foreach (var (process, parent) in Tree(pid))
        {
            if (Posix.Status(process) is { IsProcess: true, IsRunning: true } status && (parent is null || status.Parent == parent))
            {
                found.Add((process, status.StartTime));
            }
        }

        return found;
    }

    private static void Signal(List<(int Pid, long StartTime)> processes, int signal)
    {
        foreach (var (process, startTime) in processes)
        {
            Posix.SignalIf(process, signal, status => status.StartTime == startTime);
        }
    }

    /// <summary><paramref name="pid"/> and every process below it, parents first, each with the parent it was found under.</summary>
    private static List<(int Pid, int? Parent)> Tree(int pid)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var entry in EnumerateProc())
        {
            if (Parent(entry) is { } parent)
            {
                (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(entry);
            }
        }

        var result = new List<(int Pid, int? Parent)> { (pid, null) };
        for (var i = 0; i < result.Count; i++)
        {
            var parent = result[i].Pid;
            result.AddRange((children.GetValueOrDefault(parent) ?? []).Select(child => (child, (int?)parent)));
        }

        return result;
    }

    private static IEnumerable<int> EnumerateProc()
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                yield return pid;
            }
        }
    }

    private static int? Parent(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // After the command name (in parentheses, may hold spaces): state, then the parent pid.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return int.Parse(fields[1], CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
