using System.Text;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>
/// A run's output file read from its end: its complete lines, newest first, a chunk at a time, so a
/// reader that needs only the last few (a session's activity, M5f) doesn't read the whole file.
/// Bounded either way: it scans at most <see cref="MaxScanBytes"/> from the end, and skips a line
/// longer than <see cref="MaxLineBytes"/> without keeping or decoding it (the agent can write the
/// file itself, docs/AGENT-USER.md).
/// </summary>
public static class OutputLines
{
    /// <summary>How far from its end a file is read at most: 1 MiB.</summary>
    public const int MaxScanBytes = 1024 * 1024;

    /// <summary>The longest line kept: 256 KiB; a longer one is skipped.</summary>
    public const int MaxLineBytes = 256 * 1024;

    private const int ChunkSize = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The complete lines of a file's last <paramref name="maxScanBytes"/>, last first, without those
    /// longer than <paramref name="maxLineBytes"/>; none if it's missing or not a regular file of its
    /// own (as <see cref="SupervisedAgent"/> reads it). What follows the last line break is a line
    /// still being written, and what starts before the scanned part is cut: both left out. A file
    /// that shrinks while it's read ends the lines there. Lazy: stop enumerating and the rest is
    /// never read.
    /// </summary>
    public static IEnumerable<string> Backwards(
        string path, int chunkSize = ChunkSize, int maxScanBytes = MaxScanBytes, int maxLineBytes = MaxLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        using var file = Posix.OpenRegularFile(path);
        if (file is null)
        {
            yield break;
        }

        var position = file.Length;
        var floor = Math.Max(0, position - maxScanBytes);
        // The end of the line being read, found in later chunks: oldest part first.
        var pending = new List<byte[]>();
        var pendingLength = 0;
        // Whether a line break follows what's pending, i.e. it's a whole line once its start is found.
        var terminated = false;
        // The line being read is too long: its parts are dropped and it's skipped.
        var oversized = false;
        while (position > floor)
        {
            var size = (int)Math.Min(chunkSize, position - floor);
            position -= size;
            var chunk = new byte[size];
            if (!TryRead(file, position, chunk))
            {
                yield break;
            }

            var end = size;
            for (var i = size - 1; i >= 0; i--)
            {
                if (chunk[i] != (byte)'\n')
                {
                    continue;
                }

                if (terminated && !oversized && pendingLength + (end - i - 1) <= maxLineBytes)
                {
                    yield return Decode(chunk.AsSpan(i + 1, end - i - 1), pending);
                }

                pending.Clear();
                pendingLength = 0;
                terminated = true;
                oversized = false;
                end = i;
            }

            if (oversized)
            {
                continue;
            }

            if (pendingLength + end > maxLineBytes)
            {
                oversized = true;
                pending.Clear();
                pendingLength = 0;
                continue;
            }

            pending.Insert(0, chunk[..end]);
            pendingLength += end;
        }

        // The file's first line, unless the scan stopped short of the file's start.
        if (position == 0 && terminated && !oversized)
        {
            yield return Decode([], pending);
        }
    }

    /// <summary>Fills <paramref name="chunk"/> from <paramref name="position"/>; false when the file no longer has those bytes.</summary>
    private static bool TryRead(FileStream file, long position, byte[] chunk)
    {
        try
        {
            file.Seek(position, SeekOrigin.Begin);
            var read = 0;
            while (read < chunk.Length)
            {
                var count = file.Read(chunk, read, chunk.Length - read);
                if (count == 0)
                {
                    return false;
                }

                read += count;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A line: its start, then the parts that follow it. Splitting on <c>\n</c> never cuts a UTF-8 character.</summary>
    private static string Decode(ReadOnlySpan<byte> start, List<byte[]> rest)
    {
        if (rest.Count == 0)
        {
            return Utf8.GetString(start);
        }

        var line = new byte[start.Length + rest.Sum(part => part.Length)];
        start.CopyTo(line);
        var at = start.Length;
        foreach (var part in rest)
        {
            part.CopyTo(line, at);
            at += part.Length;
        }

        return Utf8.GetString(line);
    }
}
