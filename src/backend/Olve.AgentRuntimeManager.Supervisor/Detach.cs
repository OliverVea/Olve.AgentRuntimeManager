using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Cuts the supervisor loose from ARM, so ARM exiting (or a Ctrl+C sent to its process group)
/// leaves it and its agent running: a session of its own, stdio that isn't ARM's, and none of the
/// descriptors ARM's process leaked to it (e.g. a pipe its own parent never closed, which would
/// otherwise stay open as long as the agent runs).
/// </summary>
internal static class Detach
{
    public static void FromParent()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        // Fails only if already a process-group leader, which then is detached enough.
        Posix.SetSid();
        using var devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite);
        var fd = (int)devNull.DangerousGetHandle();
        for (var target = 0; target <= 2; target++)
        {
            Posix.Dup2(fd, target);
        }

        CloseInherited();
    }

    /// <summary>
    /// Closes the descriptors inherited from ARM. .NET opens its own close-on-exec, so an open
    /// descriptor above stderr without the flag was inherited.
    /// </summary>
    private static void CloseInherited()
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries("/proc/self/fd");
        }
        catch (IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (int.TryParse(Path.GetFileName(entry), out var descriptor) && descriptor > 2
                && Posix.Fcntl(descriptor, Posix.GetFdFlags, 0) is var flags and >= 0
                && (flags & Posix.CloseOnExec) == 0)
            {
                Posix.Close(descriptor);
            }
        }
    }
}
