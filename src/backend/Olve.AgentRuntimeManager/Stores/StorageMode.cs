namespace Olve.AgentRuntimeManager.Stores;

/// <summary>
/// Controls whether an <see cref="EntityStorePersister{T}"/> loads and saves snapshots.
/// </summary>
/// <remarks>
/// This module is written at library quality so it can be promoted to
/// <c>Olve.Utilities.Hosting</c> with a near-mechanical copy/namespace swap (see docs/DESIGN.md §1.1.2).
/// </remarks>
public enum StorageMode
{
    /// <summary>No load, no save. The store is in-memory only and ready immediately.</summary>
    Ephemeral,

    /// <summary>Load on startup and persist mutations. Requires an <see cref="ISnapshotStore"/>.</summary>
    Persistent,
}
