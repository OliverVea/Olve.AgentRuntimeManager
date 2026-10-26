using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Journal;

/// <summary>
/// The session's calls by tool-use id (<c>--journal</c>, JSON lines, append-only), so no id runs
/// twice: not across a restart of the server, nor a resume of the agent. A call is written down
/// before it runs and again with its result. An id that finished gets its result again; one that
/// started and never finished was cut off when a server died mid-call, so it may have partly
/// happened: it's refused, not run again, and the agent decides. Calls without an id aren't kept.
/// </summary>
/// <remarks>
/// Only each id's state is held in memory; a finished call's result is read back from the file
/// when its id comes again (rare: an agent doesn't repeat an id by itself), by reading the file
/// through. The file is opened for appending (<c>O_APPEND</c>), each line one write, so two servers
/// on one journal (an old one stopping while a resumed agent's starts) don't overwrite each other.
/// </remarks>
internal sealed class CallJournal : IDisposable
{
    /// <summary>A result is kept up to this many characters (the tools cap theirs well below it).</summary>
    public const int MaxText = 100_000;

    private readonly string _path;
    private readonly TextWriter _log;
    private readonly SafeFileHandle _file;
    private readonly Action<SafeFileHandle, byte[]> _write;
    private readonly Dictionary<string, CallState> _calls = [];

    /// <summary>Whether the last write may have left part of a line (it failed or wrote short).</summary>
    private bool _torn;

    /// <param name="path">The journal file.</param>
    /// <param name="log">Where a write that failed is told.</param>
    /// <param name="write">How a line is written (all of it, or throw); a test's stand-in for a disk that fails.</param>
    public CallJournal(string path, TextWriter? log = null, Action<SafeFileHandle, byte[]>? write = null)
    {
        _path = path;
        _log = log ?? TextWriter.Null;
        _write = write ?? ((file, bytes) => Native.WriteAll(file, bytes));
        if (Path.GetDirectoryName(path) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        _file = Native.OpenAppend(path);
        foreach (var entry in Read())
        {
            switch (entry)
            {
                case { Event: JournalEntry.Started } when !_calls.ContainsKey(entry.Id):
                    _calls[entry.Id] = CallState.Interrupted;
                    break;
                case { Event: JournalEntry.Finished }:
                    _calls[entry.Id] = CallState.Finished;
                    break;
            }
        }

        // A torn last line has no newline: the next entry starts on a line of its own.
        if (!EndsWithNewLine())
        {
            Native.WriteAll(_file, "\n"u8);
        }
    }

    /// <summary>
    /// Null if the call is to run: it's written down as started. Otherwise what to answer instead
    /// of running it: its result if it finished, an error if it was interrupted or runs now.
    /// Throws (IOException) if it can't be written down: then it mustn't run.
    /// </summary>
    public ToolResult? Begin(string id, string tool)
    {
        lock (_calls)
        {
            if (_calls.TryGetValue(id, out var state))
            {
                return state switch
                {
                    CallState.Finished => Result(id) ?? ToolResult.Error($"This call (tool-use id {id}) finished before, but its result can't be read back from the journal. It isn't run again."),
                    CallState.Running => ToolResult.Error($"A call with the tool-use id {id} is running now: it isn't started twice."),
                    _ => ToolResult.Error($"This call (tool-use id {id}) was interrupted: the tools server stopped while it ran, so it may have partly happened. "
                        + "It isn't run again: check what it did, then call again if it's still needed."),
                };
            }

            // Written first: a call not written down doesn't run, and isn't counted as running.
            Append(new JournalEntry(id, JournalEntry.Started, tool, DateTimeOffset.UtcNow));
            _calls[id] = CallState.Running;
            return null;
        }
    }

    /// <summary>
    /// Writes down the result of a call <see cref="Begin"/> let run. A write that fails is logged,
    /// not thrown: the call ran, and the agent gets its result (the id then reads as interrupted).
    /// </summary>
    public void Finish(string id, string tool, ToolResult result)
    {
        var kept = result.Text.Length <= MaxText
            ? result
            : result with { Text = result.Text[..MaxText] + "\n[... cut: the journal keeps a result up to its cap ...]" };
        lock (_calls)
        {
            try
            {
                Append(new JournalEntry(id, JournalEntry.Finished, tool, DateTimeOffset.UtcNow, kept.Text, kept.IsError));
                _calls[id] = CallState.Finished;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or ArgumentException or InvalidOperationException)
            {
                // Can't be written (the disk, or text JSON can't hold): it ran, so never runs again.
                _calls[id] = CallState.Interrupted;
                _log.WriteLine($"olve-arm-tools: could not write the result of {id} to the journal: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// A call that ends without a result (the server is stopping): left as started, so it reads as
    /// interrupted from now on.
    /// </summary>
    public void Abandon(string id)
    {
        lock (_calls)
        {
            _calls[id] = CallState.Interrupted;
        }
    }

    public void Dispose()
    {
        lock (_calls)
        {
            _file.Dispose();
        }
    }

    /// <summary>The recorded result of <paramref name="id"/> (its last finished entry), read from the file.</summary>
    private ToolResult? Result(string id)
    {
        ToolResult? found = null;
        foreach (var entry in Read())
        {
            if (entry is { Event: JournalEntry.Finished } && entry.Id == id)
            {
                found = new ToolResult(entry.Text ?? "", entry.IsError ?? false);
            }
        }

        return found;
    }

    /// <summary>
    /// The file's complete entries. A line that doesn't parse into one (cut off when a server died mid-write, or missing its id, event or
    /// tool) is skipped: what it said is lost, the rest stands.
    /// </summary>
    private IEnumerable<JournalEntry> Read()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, JournalJsonContext.Default.JournalEntry);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is { Id.Length: > 0, Event: JournalEntry.Started or JournalEntry.Finished, Tool: not null })
            {
                yield return entry;
            }
        }
    }

    private bool EndsWithNewLine()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private void Append(JournalEntry entry)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, JournalJsonContext.Default.JournalEntry);
        // After a write that failed partway, the line it left is torn: start on a fresh one.
        byte[] line = _torn ? [(byte)'\n', .. json, (byte)'\n'] : [.. json, (byte)'\n'];
        _torn = true;
        // One write of the whole line, to the kernel: the server dying (the case the journal is for) loses nothing.
        _write(_file, line);
        _torn = false;
    }
}
