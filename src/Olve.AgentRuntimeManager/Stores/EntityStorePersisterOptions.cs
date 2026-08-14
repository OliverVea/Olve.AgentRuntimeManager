namespace Olve.AgentRuntimeManager.Stores;

/// <summary>
/// Configuration for an <see cref="EntityStorePersister{T}"/>.
/// </summary>
/// <remarks>
/// Serialize/deserialize are supplied as delegates so the persister never calls reflection-based
/// <c>JsonSerializer</c> — the app passes its source-generated <c>JsonSerializerContext</c>, keeping
/// the whole path AOT- and trim-clean.
/// </remarks>
/// <typeparam name="T">The entity type held by the store.</typeparam>
public sealed class EntityStorePersisterOptions<T>
{
    /// <summary>The snapshot key, e.g. <c>"messages.json"</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Serializes the whole-store snapshot to bytes.</summary>
    public required Func<IReadOnlyList<T>, byte[]> Serialize { get; init; }

    /// <summary>Deserializes a snapshot. Returning <see langword="null"/> signals a malformed snapshot.</summary>
    public required Func<byte[], IReadOnlyList<T>?> Deserialize { get; init; }

    /// <summary>Whether to load/save at all. <see cref="StorageMode.Persistent"/> requires an <see cref="ISnapshotStore"/>.</summary>
    public StorageMode Mode { get; init; } = StorageMode.Persistent;

    /// <summary>
    /// The debounce window. Mutations within one window coalesce into at most one write
    /// (the "coalescing queue-of-one"). Defaults to one second.
    /// </summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromSeconds(1);
}
