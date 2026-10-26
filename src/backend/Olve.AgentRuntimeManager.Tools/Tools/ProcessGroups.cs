using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// The process groups of the calls running now: each <c>arm_bash</c> call runs in a session (and
/// group) of its own, so its command and everything that starts are stopped together, and
/// whatever stops the server stops them all (<see cref="KillAll"/>). A call's group outlives the
/// server otherwise: it isn't in the server's group, so nothing that ends the server reaches it.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ProcessGroups
{
    /// <summary>How long a group has after SIGTERM before it gets SIGKILL.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    private readonly HashSet<int> _groups = [];

    public void Add(int group)
    {
        lock (_groups)
        {
            _groups.Add(group);
        }
    }

    public void Remove(int group)
    {
        lock (_groups)
        {
            _groups.Remove(group);
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

    /// <summary>Whether anything is left in <paramref name="group"/>.</summary>
    public static bool IsAlive(int group) => group > 1 && Posix.Kill(-group, 0) == 0;

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
    public static async Task StopAsync(int group)
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
}
