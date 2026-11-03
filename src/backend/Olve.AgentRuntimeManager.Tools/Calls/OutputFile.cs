using System.Globalization;
using System.Runtime.Versioning;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// One call's whole output on disk, written as it arrives, up to <paramref name="maxBytes"/> (a
/// command like <c>yes</c> would fill the disk). Thread-safe: stdout and stderr write from their
/// own readers. A disk that fails costs the file, never the call. Only the session's user reads it.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class OutputFile(string path, long maxBytes) : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _gate = new();
    private FileStream? _stream;
    private long _written;
    private bool _full;
    private bool _closed;
    private string? _failure;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_closed || _full || _failure is not null)
            {
                return;
            }

            try
            {
                _stream ??= Open();
                var take = (int)Math.Min(maxBytes - _written, bytes.Length);
                _stream.Write(bytes[..take]);
                _written += take;
                _full = take < bytes.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failure = exception.Message;
            }
        }
    }

    /// <summary>Closes the file, ready to read; one that isn't kept is removed. Later writes are dropped.</summary>
    public void Close(bool keep)
    {
        lock (_gate)
        {
            _closed = true;
            try
            {
                _stream?.Dispose();
                _stream = null;
                if (!keep)
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
