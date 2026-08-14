using Microsoft.Extensions.DependencyInjection;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.Stores;

/// <summary>
/// Registration helpers for <see cref="EntityStorePersister{T}"/>.
/// </summary>
public static class EntityStorePersistenceExtensions
{
    /// <summary>
    /// Adds whole-snapshot persistence for an already-registered <see cref="EntityStore{T}"/>.
    /// The app supplies serialize/deserialize delegates (its source-gen <c>JsonSerializerContext</c>)
    /// and, for <see cref="StorageMode.Persistent"/>, registers an <see cref="ISnapshotStore"/> separately.
    /// </summary>
    public static IServiceCollection AddEntityStorePersistence<T>(
        this IServiceCollection services,
        string key,
        Func<IReadOnlyList<T>, byte[]> serialize,
        Func<byte[], IReadOnlyList<T>?> deserialize,
        StorageMode mode = StorageMode.Persistent,
        TimeSpan? saveInterval = null)
        where T : IHasId<Id<T>>
    {
        var options = new EntityStorePersisterOptions<T>
        {
            Key = key,
            Serialize = serialize,
            Deserialize = deserialize,
            Mode = mode,
            SaveInterval = saveInterval ?? TimeSpan.FromSeconds(1),
        };

        services.AddSingleton(sp => new EntityStorePersister<T>(
            sp.GetRequiredService<EntityStore<T>>(),
            options,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<EntityStorePersister<T>>>(),
            sp.GetService<ISnapshotStore>()));

        services.AddHostedService(sp => sp.GetRequiredService<EntityStorePersister<T>>());

        return services;
    }
}
