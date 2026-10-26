using System.Runtime.InteropServices;

namespace Olve.AgentRuntimeManager.Tools.Server;

/// <summary>
/// A SIGTERM when the agent that started the server dies (<c>PR_SET_PDEATHSIG</c>): an agent
/// killed with SIGKILL closes nothing and signals nothing, so the server would be orphaned and its
/// calls run on (the spike, docs/MILESTONES.md M10). The signal then stops the server as any
/// SIGTERM does. Linux only.
/// </summary>
/// <remarks>
/// The kernel sends it when the thread that started the server ends, not only the process: fine
/// for an agent that spawns from its main thread (Claude Code does).
/// </remarks>
internal static partial class ParentDeath
{
    private const int SetDeathSignal = 1; // PR_SET_PDEATHSIG
    private const int SigTerm = 15;

    /// <summary>
    /// Asks for the signal. False if the parent is gone already: it may have died before the ask,
    /// which then never fires (the server has a new parent, a reaper, by then). Null if it can't be
    /// asked (not Linux, or prctl refused): the server then ends with its input or a signal only.
    /// </summary>
    public static bool? Watch()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var parent = GetParent();
        if (Prctl(SetDeathSignal, SigTerm, 0, 0, 0) != 0)
        {
            return null;
        }

        return parent > 1 && GetParent() == parent;
    }

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int option, nuint argument2, nuint argument3, nuint argument4, nuint argument5);

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetParent();
}
