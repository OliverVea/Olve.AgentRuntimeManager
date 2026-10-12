using System.Text;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>
/// A run's output file read from its end: its complete lines, newest first, a chunk at a time, so a
/// reader that needs only the last few (a session's activity, M5f) doesn't read the whole file.
/// </summary>
public static class OutputLines
{
    private const int ChunkSize = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The complete lines of a file, last first; none if it's missing or not a regular file of its
    /// own (as <see cref="SupervisedAgent"/> reads it). What follows the last line break is a line
    /// still being written: left out. Lazy: stop enumerating and the rest is never read.
    /// </summary>
    public static IEnumerable<string> Backwards(string path, int chunkSize = ChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        using var file = Posix.OpenRegularFile(path);
        if (file is null)
        {
            yield break;
        }

        var position = file.Length;
        // The start of the file's next (later) line, found in later chunks: oldest part first.
        var pending = new List<byte[]>();
        // Whether a line break follows what's pending, i.e. it's a whole line once its start is found.
        var terminated = false;
        while (position > 0)
        {
            var size = (int)Math.Min(chunkSize, position);
            position -= size;
            var chunk = new byte[size];
            file.Seek(position, SeekOrigin.Begin);
            file.ReadExactly(chunk);

            var end = size;
            for (var i = size - 1; i >= 0; i--)
            {
                if (chunk[i] != (byte)'\n')
                {
                    continue;
                }

                if (terminated)
                {
                    yield return Decode(chunk.AsSpan(i + 1, end - i - 1), pending);
                }

                pending.Clear();
                terminated = true;
                end = i;
            }

            pending.Insert(0, chunk[..end]);
        }

        if (terminated)
        {
            yield return Decode([], pending);
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
