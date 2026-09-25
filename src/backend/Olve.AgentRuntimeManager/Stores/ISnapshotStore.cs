namespace Olve.AgentRuntimeManager.Stores;

/// <summary>
/// A blob storage seam: <c>key → bytes</c>. Adapters (file, S3, SQLite, …) live in the app and
/// carry their own dependencies, kept behind this zero-dependency port so they stay replaceable.
/// </summary>
public interface ISnapshotStore
{
    /// <summary>
    /// Reads the snapshot stored under <paramref name="key"/>.
    /// </summary>
    /// <returns>
    /// The stored bytes, or <see langword="null"/> when no snapshot exists yet (first run).
    /// Returning <see langword="null"/> for absence — rather than empty — is load-bearing: it lets
    /// callers distinguish "nothing saved yet" from "saved an empty set" and never overwrite good
    /// state with empty.
    /// </returns>
    Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="content"/> as the snapshot under <paramref name="key"/>.</summary>
    Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken);
}
