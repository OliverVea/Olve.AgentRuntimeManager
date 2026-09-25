namespace Olve.AgentRuntimeManager.Stores;

/// <summary>
/// A <see cref="ISnapshotStore"/> backed by the local filesystem. BCL-only — the default adapter
/// for local development and the template's out-of-the-box behaviour. Writes are atomic
/// (temp file + move) so a crash mid-write can never leave a half-written snapshot.
/// </summary>
public sealed class FileSnapshotStore(string directory) : ISnapshotStore
{
    private readonly string _directory = directory;

    /// <inheritdoc />
    public async Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);

        var path = PathFor(key);
        var tempPath = path + ".tmp";

        await File.WriteAllBytesAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private string PathFor(string key) => Path.Combine(_directory, key);
}
