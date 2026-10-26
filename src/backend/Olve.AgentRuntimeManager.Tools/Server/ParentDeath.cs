namespace Olve.AgentRuntimeManager.Tools.Server;

/// <summary>
/// A SIGTERM when the agent that started the server dies (<c>PR_SET_PDEATHSIG</c>): an agent
/// killed with SIGKILL closes nothing and signals nothing, so the server would be orphaned and its
/// calls run on (the spike, docs/MILESTONES.md M10). The signal then stops the server as any
/// SIGTERM does. Linux only.
/// </summary>
/// <remarks>
/// The kernel sends it when the thread that started the server ends, not only the process: fine
/// for an agent whose spawning thread lives as long as it does. Claude Code 2.1.310's does as far
/// as seen (2026-10-26: a server alive over a 90 s call and the call after it); a test host's
/// thread-pool thread may not, so tests keep a server's life short.
/// </remarks>
internal static class ParentDeath
{
    private const int SigTerm = 15;

    /// <summary>
    /// Asks for the signal. False if the parent isn't <paramref name="parent"/> (the one the server
    /// had when it started) any more: it died before the ask, which then never fires. Null if it
    /// can't be asked (not Linux, or prctl refused): the server then ends with its input or a signal only.
    /// </summary>
    public static bool? Watch(int parent)
    {
        if (!OperatingSystem.IsLinux() || Native.Prctl(Native.SetDeathSignal, SigTerm, 0, 0, 0) != 0)
        {
            return null;
        }

        return Native.GetParent() == parent;
    }
}
