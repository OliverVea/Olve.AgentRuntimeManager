using System.Buffers.Binary;
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
    public const int SigTerm = 15;

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
    /// Opens <paramref name="path"/> for reading without following a link in its last component,
    /// without blocking on a FIFO, and only if it's a directory (<paramref name="directory"/>) or
    /// else a regular file. Null if it's missing, a link, or of another kind. For files in a folder
    /// an agent can write (docs/AGENT-USER.md): ARM must not be made to read or change what a link
    /// points to.
    /// </summary>
    /// <remarks>
    /// <c>O_NOFOLLOW</c> isn't trusted alone: under gVisor (the pipeline's pods) <c>O_NOFOLLOW |
    /// O_DIRECTORY</c> on a link to a directory opens the target. So what was opened is verified
    /// (<see cref="IsAt"/>): the path, not followed, must be of the asked kind and the very inode the
    /// descriptor holds. A swap between the open and the check shows as another inode.
    /// </remarks>
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
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            // ENOENT, ENXIO (a socket), ENOTDIR, ELOOP (a link).
            return error is 2 or 6 or 20 or 40 ? null : throw new IOException($"Could not open '{path}' (errno {error}).");
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (IsAt(handle, path, directory))
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    /// <summary>
    /// Whether <paramref name="handle"/> holds what is at <paramref name="path"/> itself, not
    /// followed: the path is a directory (<paramref name="directory"/>) or a regular file, not a
    /// link, and it and the descriptor are the same inode on the same device. Linux only (statx).
    /// </summary>
    public static bool IsAt(SafeFileHandle handle, string path, bool directory)
    {
        var expected = directory ? TypeDirectory : TypeRegular;
        if (Lstat(path) is not { } atPath || atPath.Type != expected)
        {
            return false;
        }

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return Fstat((int)handle.DangerousGetHandle()) is { } opened
                && opened.Type == expected && opened.Device == atPath.Device && opened.Inode == atPath.Inode;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Whether anything is at <paramref name="path"/>, a link (even a dangling one) included: not followed.</summary>
    public static bool ExistsNoFollow(string path) =>
        OperatingSystem.IsLinux()
            ? Lstat(path) is not null
            : new FileInfo(path) is var info && (info.LinkTarget is not null || info.Exists || Directory.Exists(path));

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

        try
        {
            return new FileStream(handle, FileAccess.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            handle.Dispose();
            return null;
        }
    }

    /// <summary>The device and inode of what's at <paramref name="path"/> (not followed); null if nothing is there.</summary>
    public static (ulong Device, ulong Inode)? Identity(string path) =>
        Lstat(path) is { } identity ? (identity.Device, identity.Inode) : null;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenWithMode(string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int FlockCall(int fd, int operation);

    /// <summary>
    /// Opens (creating it, <c>0644</c>) a lock file for <see cref="TryLock"/>, with <c>open(2)</c> itself:
    /// .NET's own <c>FileStream</c> takes <c>flock</c> locks of its own on open, which would meet these.
    /// </summary>
    public static SafeFileHandle OpenLockFile(string path)
    {
        var noFollow = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
        const int readWrite = 0x2, create = 0x40, closeOnExec = 0x80000; // O_RDWR | O_CREAT | O_CLOEXEC
        var fd = OpenWithMode(path, readWrite | create | noFollow | closeOnExec, Convert.ToInt32("644", 8));
        return fd >= 0
            ? new SafeFileHandle(fd, ownsHandle: true)
            : throw new IOException($"Could not open the lock file {path} (errno {Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>
    /// <c>flock(LOCK_EX | LOCK_NB)</c>: true if it's ours now, false if another open file holds it
    /// (<c>EWOULDBLOCK</c>); any other failure throws. Released when the handle closes, or its
    /// process dies.
    /// </summary>
    public static bool TryLock(SafeFileHandle handle)
    {
        const int exclusive = 2, nonBlocking = 4, wouldBlock = 11, interrupted = 4; // LOCK_EX, LOCK_NB, EWOULDBLOCK, EINTR
        while (true)
        {
            if (FlockCall((int)handle.DangerousGetHandle(), exclusive | nonBlocking) == 0)
            {
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == wouldBlock)
            {
                return false;
            }

            if (error != interrupted)
            {
                throw new IOException($"flock failed (errno {error}).");
            }
        }
    }

    /// <summary>What <c>statx</c> says of a file: its type (<c>S_IFMT</c> bits of the mode), device and inode.</summary>
    private readonly record struct FileIdentity(int Type, ulong Device, ulong Inode);

    private const int TypeMask = 0xF000; // S_IFMT
    private const int TypeDirectory = 0x4000; // S_IFDIR
    private const int TypeRegular = 0x8000; // S_IFREG

    /// <summary>The file at <paramref name="path"/> itself (a link is a link); null if there's nothing there.</summary>
    private static FileIdentity? Lstat(string path)
    {
        const int currentDirectory = -100, symlinkNoFollow = 0x100; // AT_FDCWD, AT_SYMLINK_NOFOLLOW
        return Statx(currentDirectory, path, symlinkNoFollow);
    }

    /// <summary>The file a descriptor holds.</summary>
    private static FileIdentity? Fstat(int fd)
    {
        const int emptyPath = 0x1000, symlinkNoFollow = 0x100; // AT_EMPTY_PATH
        return Statx(fd, "", emptyPath | symlinkNoFollow);
    }

    /// <summary>
    /// <c>statx(2)</c>: its <c>struct statx</c> has one layout on every architecture (unlike
    /// <c>struct stat</c>): <c>stx_mask</c> at 0 (u32), <c>stx_mode</c> at 28 (u16), <c>stx_ino</c>
    /// at 32 (u64), <c>stx_dev_major</c>/<c>stx_dev_minor</c> at 136/140 (u32); 256 bytes in all.
    /// </summary>
    private static FileIdentity? Statx(int directoryFd, string path, int flags)
    {
        const uint wanted = 0x1 | 0x100; // STATX_TYPE | STATX_INO
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();
        if (StatxCall(directoryFd, path, flags, wanted, ref MemoryMarshal.GetReference(buffer)) != 0)
        {
            return null;
        }

        var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..]);
        if ((mask & wanted) != wanted)
        {
            return null;
        }

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[28..]);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer[32..]);
        var device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(buffer[136..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(buffer[140..]);
        return new FileIdentity(mode & TypeMask, device, inode);
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int StatxCall(int directoryFd, string path, int flags, uint mask, ref byte buffer);

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
