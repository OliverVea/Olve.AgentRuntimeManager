using System.Globalization;
using System.Runtime.InteropServices;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>The few POSIX calls .NET doesn't offer: detaching, redirecting stdio, signalling a process group.</summary>
public static partial class Posix
{
    public const int SigKill = 9;

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    public static partial int SetSid();

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    public static partial int Dup2(int oldFd, int newFd);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    public static partial int Fcntl(int fd, int command, int argument);

    /// <summary><c>fcntl</c>'s <c>F_GETFD</c>, and its <c>FD_CLOEXEC</c> flag.</summary>
    public const int GetFdFlags = 1;
    public const int CloseOnExec = 1;

    /// <summary>
    /// When a process started, in clock ticks since boot (<c>/proc/&lt;pid&gt;/stat</c>, field 22);
    /// null if it isn't running (or this isn't Linux). A pid plus its start time names one process.
    /// </summary>
    public static long? StartTime(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // The command name (field 2) is in parentheses and may contain spaces: count from after it.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return long.Parse(fields[19], CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
