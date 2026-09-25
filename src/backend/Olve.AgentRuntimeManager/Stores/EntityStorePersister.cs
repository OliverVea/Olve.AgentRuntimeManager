using Microsoft.Extensions.Hosting;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.Stores;

/// <summary>
/// Persists an <see cref="EntityStore{T}"/> as a whole-snapshot blob: loads it on startup, saves a
/// debounced snapshot on mutation, and flushes on shutdown. Encapsulates the safety lifecycle so it
/// can never overwrite good state with empty (see docs/DESIGN.md §1.1.2).
/// </summary>
/// <remarks>
/// <para>
/// The persister is an <em>added layer</em>: an <see cref="EntityStore{T}"/> with no persister
/// registered behaves exactly as a plain in-memory store.
/// </para>
/// <para>
/// v1 is whole-snapshot and single-store. Per-entity/delta saves and multi-store snapshots are out
/// of scope (the events already carry <see cref="Id{T}"/>, so deltas are a later optimisation).
/// </para>
/// </remarks>
/// <typeparam name="T">The entity type; must expose an <see cref="Id{T}"/>.</typeparam>
public sealed class EntityStorePersister<T> : IHostedLifecycleService, IDisposable
    where T : IHasId<Id<T>>
{
    private readonly EntityStore<T> _store;
    private readonly EntityStorePersisterOptions<T> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EntityStorePersister<T>> _logger;
    private readonly ISnapshotStore? _snapshotStore;

    // Serializes writes so a debounced flush and the shutdown flush can never overlap.
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Action<Id<T>> _onChanged;

    private ITimer? _timer;

    // Two-phase ready state: _loading suppresses the echo while repopulating; _loaded gates all saves.
    private volatile bool _loading;
    private volatile bool _loaded;
    private int _dirty;

    /// <summary>Creates a persister. <paramref name="snapshotStore"/> may be <see langword="null"/> for ephemeral mode.</summary>
    public EntityStorePersister(
        EntityStore<T> store,
        EntityStorePersisterOptions<T> options,
        TimeProvider timeProvider,
        ILogger<EntityStorePersister<T>> logger,
        ISnapshotStore? snapshotStore = null)
    {
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _snapshotStore = snapshotStore;
        _onChanged = _ => RequestSave();
    }

    /// <summary>Loads the snapshot before the host starts serving. A load failure throws (crashloop) rather than risk overwriting good state.</summary>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode == StorageMode.Ephemeral)
        {
            // No load, no save; ready immediately. An ISnapshotStore may be absent.
            _loaded = true;
            return;
        }

        if (_snapshotStore is null)
        {
            throw new InvalidOperationException(
                $"Persistent storage for '{typeof(T).Name}' requires an {nameof(ISnapshotStore)} to be registered.");
        }

        // Subscribe before loading so nothing is missed; the _loading guard suppresses the echo.
        _store.OnAdded.Subscribe(_onChanged);
        _store.OnUpdated.Subscribe(_onChanged);
        _store.OnDeleted.Subscribe(_onChanged);

        _loading = true;
        try
        {
            // A throw here (transient/auth) propagates out of startup → crashloop. We never save.
            var bytes = await _snapshotStore.TryReadAsync(_options.Key, cancellationToken);

            if (bytes is null)
            {
                // First run: an empty baseline is safe to write.
                _loaded = true;
                _loading = false;
                await SaveCoreAsync(cancellationToken);
                return;
            }

            IReadOnlyList<T>? entities;
            try
            {
                entities = _options.Deserialize(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "Snapshot {Key} for {Type} is corrupt and could not be deserialized; manual restore required.",
                    _options.Key, typeof(T).Name);
                throw;
            }

            if (entities is null)
            {
                _logger.LogCritical(
                    "Snapshot {Key} for {Type} deserialized to null (malformed); manual restore required.",
                    _options.Key, typeof(T).Name);
                throw new InvalidDataException($"Snapshot '{_options.Key}' for {typeof(T).Name} is malformed.");
            }

            // Repopulate under the _loading guard so these Set calls don't echo back as saves.
            foreach (var entity in entities)
            {
                _store.Set(entity);
            }

            // Mark loaded while still _loading so no save can slip in before we clear the guard.
            _loaded = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Starts the debounce timer once the snapshot is loaded.</summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode == StorageMode.Persistent)
        {
            _timer = _timeProvider.CreateTimer(_ => OnTick(), null, _options.SaveInterval, _options.SaveInterval);
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the timer and performs a final flush (subject to the write-gate).</summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }

        await SaveAsync(cancellationToken);
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken) => StartedAsync(cancellationToken);
    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken) => StartingAsync(cancellationToken);
    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) => StoppingAsync(cancellationToken);
    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Marks the store dirty so the next timer tick flushes it. No-op until the load is confirmed.</summary>
    public void RequestSave()
    {
        if (_loading || !_loaded || _options.Mode != StorageMode.Persistent)
        {
            return;
        }

        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Flushes immediately if there are unsaved changes and the load is confirmed.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_loading || !_loaded)
        {
            return;
        }

        await SaveCoreAsync(cancellationToken);
    }

    private void OnTick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            _ = FlushAsync();
        }
    }

    private async Task FlushAsync()
    {
        try
        {
            await SaveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Re-arm so the next tick retries rather than silently dropping the change.
            Interlocked.Exchange(ref _dirty, 1);
            _logger.LogError(ex, "Failed to persist snapshot {Key} for {Type}; will retry.",
                _options.Key, typeof(T).Name);
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        if (_snapshotStore is null)
        {
            return;
        }

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var bytes = _options.Serialize(_store.List());
            await _snapshotStore.WriteAsync(_options.Key, bytes, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _store.OnAdded.Unsubscribe(_onChanged);
        _store.OnUpdated.Unsubscribe(_onChanged);
        _store.OnDeleted.Unsubscribe(_onChanged);
        _saveGate.Dispose();
    }
}
