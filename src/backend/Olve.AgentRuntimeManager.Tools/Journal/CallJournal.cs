using System.Text;
using System.Text.Json;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Journal;

/// <summary>
/// The session's calls by tool-use id (<c>--journal</c>, JSON lines, append-only), so no id runs
/// twice: not across a restart of the server, nor a resume of the agent. A call is written down
/// before it runs and again with its result. An id that finished gets its result again; one that
/// started and never finished was cut off when a server died mid-call, so it may have partly
/// happened: it's refused, not run again, and the agent decides. Calls without an id aren't kept.
/// </summary>
internal sealed class CallJournal : IDisposable
{
    /// <summary>A result is kept up to this many characters (the tools cap theirs well below it).</summary>
    public const int MaxText = 100_000;

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly FileStream _file;
    private readonly Dictionary<string, ToolResult?> _calls = [];
    private readonly HashSet<string> _running = [];

    public CallJournal(string path)
    {
        if (Path.GetDirectoryName(path) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        Load();
        _file.Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// Null if the call is to run: it's written down as started. Otherwise what to answer instead
    /// of running it: its result if it finished, an error if it was interrupted or runs now.
    /// </summary>
    public ToolResult? Begin(string id, string tool)
    {
        lock (_calls)
        {
            if (_calls.TryGetValue(id, out var result))
            {
                return result
                    ?? (_running.Contains(id)
                        ? ToolResult.Error($"A call with the tool-use id {id} is running now: it isn't started twice.")
                        : ToolResult.Error($"This call (tool-use id {id}) was interrupted: the tools server stopped while it ran, so it may have partly happened. "
                            + "It isn't run again: check what it did, then call again if it's still needed."));
            }

            _calls[id] = null;
            _running.Add(id);
            Append(new JournalEntry(id, JournalEntry.Started, tool, DateTimeOffset.UtcNow));
            return null;
        }
    }

    /// <summary>Writes down the result of a call <see cref="Begin"/> let run.</summary>
    public void Finish(string id, string tool, ToolResult result)
    {
        var kept = result.Text.Length <= MaxText
            ? result
            : result with { Text = result.Text[..MaxText] + "\n[... cut: the journal keeps a result up to its cap ...]" };
        lock (_calls)
        {
            _calls[id] = kept;
            _running.Remove(id);
            Append(new JournalEntry(id, JournalEntry.Finished, tool, DateTimeOffset.UtcNow, kept.Text, kept.IsError));
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
            _running.Remove(id);
        }
    }

    public void Dispose()
    {
        lock (_calls)
        {
            _file.Dispose();
        }
    }

    private void Load()
    {
        using var reader = new StreamReader(_file, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
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
                // A line cut off when the server died mid-write: what it said is lost, the rest stands.
                continue;
            }

            switch (entry)
            {
                case { Event: JournalEntry.Started } when !_calls.ContainsKey(entry.Id):
                    _calls[entry.Id] = null;
                    break;
                case { Event: JournalEntry.Finished }:
                    _calls[entry.Id] = new ToolResult(entry.Text ?? "", entry.IsError ?? false);
                    break;
            }
        }

        // A torn last line has no newline: the next entry starts on a line of its own.
        if (EndsWithoutNewLine())
        {
            _file.Seek(0, SeekOrigin.End);
            _file.Write(NewLine);
            _file.Flush();
        }
    }

    private bool EndsWithoutNewLine()
    {
        if (_file.Length == 0)
        {
            return false;
        }

        _file.Seek(-1, SeekOrigin.End);
        return _file.ReadByte() != '\n';
    }

    private void Append(JournalEntry entry)
    {
        _file.Write(JsonSerializer.SerializeToUtf8Bytes(entry, JournalJsonContext.Default.JournalEntry));
        _file.Write(NewLine);
        // To the kernel, not only our buffer: the server dying (the case the journal is for) loses nothing.
        _file.Flush();
    }
}
