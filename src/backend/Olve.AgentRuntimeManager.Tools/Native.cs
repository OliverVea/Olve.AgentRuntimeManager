using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Olve.AgentRuntimeManager.Tools;

/// <summary>
/// The POSIX calls the tools need beyond the supervisor's <c>Posix</c>: prctl, rename, waitpid,
/// an append-only open and a file's kind. Linux.
/// </summary>
internal static partial class Native
{
    public const int SetDeathSignal = 1; // PR_SET_PDEATHSIG
    public const int SetChildSubreaper = 36; // PR_SET_CHILD_SUBREAPER
    public const int CrossDevice = 18; // EXDEV

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    public static partial int Prctl(int option, nuint argument2, nuint argument3, nuint argument4, nuint argument5);

    [LibraryImport("libc", EntryPoint = "getppid")]
    public static partial int GetParent();

    [LibraryImport("libc", EntryPoint = "getpid")]
    public static partial int GetPid();

    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameCall(string from, string to);

    /// <summary>rename(2): the entry itself (a link stays a link), replacing a file or link at <paramref name="to"/>; 0 or the errno.</summary>
    public static int Rename(string from, string to) => RenameCall(from, to) == 0 ? 0 : Marshal.GetLastPInvokeError();

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int WaitPid(int pid, out int status, int options);

    /// <summary>Reaps <paramref name="pid"/> if it's a child that has ended (WNOHANG: never waits).</summary>
    public static void Reap(int pid) => WaitPid(pid, out _, 1);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint Write(int fd, byte* buffer, nint count);

    /// <summary>
    /// Opens <paramref name="path"/> for appending (<c>O_APPEND</c>, created <c>0644</c>): every write
    /// goes to the end as it is then, so two writers never overwrite each other's lines.
    /// </summary>
    public static SafeFileHandle OpenAppend(string path)
    {
        const int writeOnly = 0x1, create = 0x40, append = 0x400, closeOnExec = 0x80000;
        var fd = Open(path, writeOnly | create | append | closeOnExec, Convert.ToInt32("644", 8));
        return fd >= 0
            ? new SafeFileHandle(fd, ownsHandle: true)
            : throw new IOException($"Could not open {path} for appending (errno {Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>All of <paramref name="bytes"/>, in one write(2) where the kernel takes it so (it does for a file).</summary>
    public static unsafe void WriteAll(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        const int interrupted = 4;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            fixed (byte* start = bytes)
            {
                var done = 0;
                while (done < bytes.Length)
                {
                    var written = Write(fd, start + done, bytes.Length - done);
                    if (written < 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == interrupted)
                        {
                            continue;
                        }

                        throw new IOException($"write failed (errno {error}).");
                    }

                    done += (int)written;
                }
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directoryFd, string path, int flags, uint mask, ref byte buffer);

    /// <summary>
    /// What <paramref name="path"/> is, links followed (statx: <c>stx_mode</c> at 28, one layout on
    /// every architecture); null if nothing is there.
    /// </summary>
    public static FileKind? Kind(string path)
    {
        const int currentDirectory = -100; // AT_FDCWD
        const uint type = 0x1; // STATX_TYPE
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();
        if (Statx(currentDirectory, path, 0, type, ref MemoryMarshal.GetReference(buffer)) != 0)
        {
            return null;
        }

        return (BinaryPrimitives.ReadUInt16LittleEndian(buffer[28..]) & 0xF000) switch
        {
            0x8000 => FileKind.Regular,
            0x4000 => FileKind.Directory,
            0x1000 => FileKind.Fifo,
            0xC000 => FileKind.Socket,
            0x2000 or 0x6000 => FileKind.Device,
            _ => FileKind.Other,
        };
    }
}
