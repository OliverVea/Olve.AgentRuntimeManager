using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>
/// What a test started and may have left running: supervisors detach themselves (docs/GENTLE-RESTART.md),
/// so one a test didn't end would outlive it, and keep a test run from finishing.
/// </summary>
public static class TestProcesses
{
    /// <summary>
    /// Kills every supervisor (and its agent) whose <c>supervisor.json</c> is in a session folder under
    /// <paramref name="root"/>, if its pid still names a supervisor (a pid can be reused).
    /// </summary>
    public static void KillSupervisors(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, SupervisorFiles.Info, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
        {
            if (SupervisorFiles.ReadInfo(Path.GetDirectoryName(file)!) is not { } info)
            {
                continue;
            }

            if (info.SupervisorPid > 1 && IsSupervisor(info.SupervisorPid))
            {
                Posix.Kill(-info.SupervisorPid, Posix.SigKill);
                Posix.Kill(info.SupervisorPid, Posix.SigKill);
            }

            if (info.AgentPid > 1 && Posix.StartTime(info.AgentPid) is { } started && started == info.AgentStartTime)
            {
                Posix.Kill(info.AgentPid, Posix.SigKill);
            }
        }
    }

    private static bool IsSupervisor(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline").Contains("olve-arm-supervisor", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
