using System.Globalization;
using System.Runtime.Versioning;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// One call's whole output on disk, written as it arrives, up to <paramref name="maxBytes"/> (a
/// command like <c>yes</c> would fill the disk). The first <see cref="ToolOutputs.MaxText"/> bytes
/// are held in memory: output that short is never cut, so it never needs a file. Thread-safe:
/// stdout and stderr write from their own readers. A disk that fails costs the file, never the
/// call. Only the session's user reads it.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class OutputFile(string path, long maxBytes) : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _gate = new();
    private readonly MemoryStream _held = new();
    private FileStream? _stream;
    private long _written;
    private bool _full;
    private bool _closed;
    private bool _complete = true;
    private string? _failure;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_closed || _full || _failure is not null)
            {
                return;
            }

            var take = (int)Math.Min(maxBytes - _written, bytes.Length);
            _written += take;
            _full = take < bytes.Length;
            try
            {
                if (_stream is null && _held.Length + take <= ToolOutputs.MaxText)
                {
                    _held.Write(bytes[..take]);
                    return;
                }

                if (_stream is null)
                {
                    _stream = Open();
                    _held.WriteTo(_stream);
                }

                _stream.Write(bytes[..take]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failure = exception.Message;
            }
        }
    }

    /// <summary>
    /// Closes the file, ready to read; one that isn't kept is removed (or never made). Later writes
    /// are dropped. <paramref name="complete"/>: whether all of the output had arrived.
    /// </summary>
    public void Close(bool keep, bool complete = true)
    {
        lock (_gate)
        {
            _closed = true;
            _complete = complete;
            try
            {
                if (keep && _stream is null && _failure is null)
                {
                    _stream = Open();
                    _held.WriteTo(_stream);
                }

                _stream?.Dispose();
                _stream = null;
                if (!keep && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failure ??= exception.Message;
            }
        }
    }

    /// <summary>For the agent: where the output cut from a result can be read.</summary>
    public string Note()
    {
        lock (_gate)
        {
            return _failure is not null ? $"it couldn't be saved to a file ({_failure})"
                : _full ? string.Create(CultureInfo.InvariantCulture, $"the first {maxBytes / 1_000_000} MB of it are in {path}; read it in parts")
                : !_complete ? $"what had arrived of it is in {path}; read it in parts"
                : $"the whole of it is in {path}; read it in parts";
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
                // The last of it didn't reach the disk: the call is over either way.
            }
        }
    }

    private FileStream Open()
    {
        if (Path.GetDirectoryName(path) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerOnly,
        });
    }
}
