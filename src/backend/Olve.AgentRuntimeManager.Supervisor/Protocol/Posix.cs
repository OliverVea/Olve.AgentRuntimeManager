using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// The few POSIX calls .NET doesn't offer: detaching, redirecting stdio, signalling a process group,
/// who we are and who is on the other end of a socket.
/// </summary>
public static partial class Posix
{
    public const int SigKill = 9;

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    public static partial int SetSid();

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    public static partial int Dup2(int oldFd, int newFd);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);

    /// <summary>
    /// Sends <paramref name="signal"/> to <paramref name="pid"/> (a process group when negative):
    /// true if it was sent or there's no such process (any more); false otherwise, with the errno
    /// (EPERM: a process of another user).
    /// </summary>
    public static bool SignalOrGone(int pid, int signal, out int error)
    {
        const int noSuchProcess = 3;
        error = Kill(pid, signal) == 0 ? 0 : Marshal.GetLastPInvokeError();
        return error is 0 or noSuchProcess;
    }

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    /// <summary>
    /// Opens <paramref name="path"/> for reading without following a link in its last component
    /// (<c>O_NOFOLLOW</c>), without blocking on a FIFO, and only if it's a directory when
    /// <paramref name="directory"/> is set. Null if it's missing or is a link (or, for a directory,
    /// isn't one). For files in a folder an agent can write (docs/AGENT-USER.md): ARM must not be
    /// made to read or change what a link points to.
    /// </summary>
    public static SafeFileHandle? OpenNoFollow(string path, bool directory = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            // Local dev elsewhere (the agent user is Linux only): a plain check is enough for files.
            if (directory)
            {
                throw new PlatformNotSupportedException("Opening a directory without following links needs Linux.");
            }

            return new FileInfo(path).LinkTarget is null && File.Exists(path)
                ? File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                : null;
        }

        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var noFollow = arm64 ? 0x8000 : 0x20000;
        var onlyDirectory = arm64 ? 0x4000 : 0x10000;
        const int readOnly = 0, nonBlocking = 0x800, closeOnExec = 0x80000;
        var fd = Open(path, readOnly | nonBlocking | closeOnExec | noFollow | (directory ? onlyDirectory : 0));
        if (fd >= 0)
        {
            return new SafeFileHandle(fd, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        // ENOENT, ENXIO (a socket), ENOTDIR, ELOOP (a link).
        return error is 2 or 6 or 20 or 40 ? null : throw new IOException($"Could not open '{path}' (errno {error}).");
    }

    /// <summary>Whether anything is at <paramref name="path"/>, a link (even a dangling one) included: not followed.</summary>
    public static bool ExistsNoFollow(string path) =>
        new FileInfo(path) is var info && (info.LinkTarget is not null || info.Exists || Directory.Exists(path));

    /// <summary>
    /// A session file opened for reading (<see cref="OpenNoFollow"/>); null if it's missing, a link,
    /// or not a regular file (a FIFO would block its reader, a directory can't be read).
    /// </summary>
    public static FileStream? OpenRegularFile(string path)
    {
        if (OpenNoFollow(path) is not { } handle)
        {
            return null;
        }

        FileStream? file = null;
        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.Directory) == 0)
            {
                file = new FileStream(handle, FileAccess.Read);
                if (file.CanSeek)
                {
                    return file;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        file?.Dispose();
        handle.Dispose();
        return null;
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    public static partial int GetEUid();

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    public static partial int Fcntl(int fd, int command, int argument);

    /// <summary><c>fcntl</c>'s <c>F_GETFD</c>, and its <c>FD_CLOEXEC</c> flag.</summary>
    public const int GetFdFlags = 1;
    public const int CloseOnExec = 1;

    /// <summary>
    /// The user id of the process on the other end of a connected Unix socket (<c>SO_PEERCRED</c>),
    /// as the kernel saw it when the connection was made; null where that can't be asked (not Linux).
    /// </summary>
    public static int? PeerUid(System.Net.Sockets.Socket socket)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        Span<byte> credentials = stackalloc byte[12];
        return socket.GetRawSocketOption(SolSocket, SoPeerCred, credentials) == credentials.Length
            ? BitConverter.ToInt32(credentials[4..8])
            : null;
    }

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>
    /// When a process started, in clock ticks since boot (<c>/proc/&lt;pid&gt;/stat</c>, field 22);
    /// null if it isn't running (or this isn't Linux). A pid plus its start time names one process.
    /// A zombie isn't running: it has ended, and only waits for its parent to reap it (which, for an
    /// orphan in a container whose pid 1 doesn't reap, may never happen).
    /// </summary>
    public static long? StartTime(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // The command name (field 2) is in parentheses and may contain spaces: count from after it.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[0] is "Z" or "X" ? null : long.Parse(fields[19], CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
