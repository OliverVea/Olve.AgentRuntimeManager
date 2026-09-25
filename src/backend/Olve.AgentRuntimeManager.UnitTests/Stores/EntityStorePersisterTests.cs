using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Stores;
using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.UnitTests.Stores;

/// <summary>A minimal entity for exercising the generic persister independent of the Message feature.</summary>
public sealed record TestEntity(Id<TestEntity> Id, string Name) : IHasId<Id<TestEntity>>;

/// <summary>
/// The §1.1.2 acceptance criteria, each a test against a faked <see cref="ISnapshotStore"/>.
/// </summary>
public class EntityStorePersisterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static EntityStorePersister<TestEntity> Create(
        EntityStore<TestEntity> store,
        FakeSnapshotStore? snapshotStore,
        FakeTimeProvider time,
        StorageMode mode = StorageMode.Persistent,
        Func<byte[], IReadOnlyList<TestEntity>?>? deserialize = null)
    {
        var options = new EntityStorePersisterOptions<TestEntity>
        {
            Key = "test.json",
            Serialize = list => JsonSerializer.SerializeToUtf8Bytes(list),
            Deserialize = deserialize ?? (bytes => JsonSerializer.Deserialize<List<TestEntity>>(bytes)),
            Mode = mode,
            SaveInterval = Interval,
        };

        return new EntityStorePersister<TestEntity>(
            store, options, time, NullLogger<EntityStorePersister<TestEntity>>.Instance, snapshotStore);
    }

    private static byte[] Serialize(params TestEntity[] entities) =>
        JsonSerializer.SerializeToUtf8Bytes((IReadOnlyList<TestEntity>)entities);

    private static TestEntity NewEntity(string name) => new(Id.New<TestEntity>(), name);

    [Test]
    public async Task Ephemeral_DoesNotLoadOrSave_AndIsReadyImmediately()
    {
        var store = new EntityStore<TestEntity>([]);
        var snapshotStore = new FakeSnapshotStore();
        var time = new FakeTimeProvider();
        var persister = Create(store, snapshotStore, time, StorageMode.Ephemeral);

        await persister.StartingAsync(CancellationToken.None);
        await persister.StartedAsync(CancellationToken.None);

        store.Set(NewEntity("a"));
        time.Advance(Interval);

        await Assert.That(snapshotStore.Reads).IsEqualTo(0);
        await Assert.That(snapshotStore.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task Persistent_WithoutSnapshotStore_FailsFast()
    {
        var persister = Create(new EntityStore<TestEntity>([]), snapshotStore: null, new FakeTimeProvider());

        await Assert.That(async () => await persister.StartingAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task LoadFailure_Throws_AndNeverSaves()
    {
        var snapshotStore = new FakeSnapshotStore { ReadOverride = () => throw new IOException("transient") };
        var persister = Create(new EntityStore<TestEntity>([]), snapshotStore, new FakeTimeProvider());

        await Assert.That(async () => await persister.StartingAsync(CancellationToken.None)).Throws<IOException>();
        await Assert.That(snapshotStore.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task CorruptSnapshot_DeserializeThrows_Throws()
    {
        var snapshotStore = new FakeSnapshotStore { Stored = "}{ not json"u8.ToArray() };
        var persister = Create(new EntityStore<TestEntity>([]), snapshotStore, new FakeTimeProvider(),
            deserialize: _ => throw new JsonException("corrupt"));

        await Assert.That(async () => await persister.StartingAsync(CancellationToken.None)).Throws<JsonException>();
        await Assert.That(snapshotStore.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedSnapshot_DeserializeReturnsNull_Throws()
    {
        var snapshotStore = new FakeSnapshotStore { Stored = "[]"u8.ToArray() };
        var persister = Create(new EntityStore<TestEntity>([]), snapshotStore, new FakeTimeProvider(),
            deserialize: _ => null);

        await Assert.That(async () => await persister.StartingAsync(CancellationToken.None))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task FirstRun_NullRead_PopulatesEmptyAndWritesBaseline()
    {
        var store = new EntityStore<TestEntity>([]);
        var snapshotStore = new FakeSnapshotStore { Stored = null };
        var persister = Create(store, snapshotStore, new FakeTimeProvider());

        await persister.StartingAsync(CancellationToken.None);

        await Assert.That(store.List().Count).IsEqualTo(0);
        await Assert.That(snapshotStore.Writes).IsEqualTo(1);
    }

    [Test]
    public async Task SuccessfulLoad_PopulatesStore_WithoutEchoingBackAsSave()
    {
        var store = new EntityStore<TestEntity>([]);
        var snapshotStore = new FakeSnapshotStore { Stored = Serialize(NewEntity("a"), NewEntity("b")) };
        var persister = Create(store, snapshotStore, new FakeTimeProvider());

        await persister.StartingAsync(CancellationToken.None);

        await Assert.That(store.List().Count).IsEqualTo(2);
        await Assert.That(snapshotStore.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task RequestSaveAndSaveAsync_BeforeLoad_AreNoOps()
    {
        var snapshotStore = new FakeSnapshotStore();
        var persister = Create(new EntityStore<TestEntity>([]), snapshotStore, new FakeTimeProvider());

        persister.RequestSave();
        await persister.SaveAsync(CancellationToken.None);

        await Assert.That(snapshotStore.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task Debounce_CoalescesMultipleMutations_IntoOneWrite()
    {
        var store = new EntityStore<TestEntity>([]);
        var snapshotStore = new FakeSnapshotStore { Stored = Serialize(NewEntity("seed")) };
        var time = new FakeTimeProvider();
        var persister = Create(store, snapshotStore, time);

        await persister.StartingAsync(CancellationToken.None);
        await persister.StartedAsync(CancellationToken.None);
        var writesAfterLoad = snapshotStore.Writes;

        store.Set(NewEntity("a"));
        store.Set(NewEntity("b"));
        store.Set(NewEntity("c"));
        time.Advance(Interval);

        await Assert.That(snapshotStore.Writes - writesAfterLoad).IsEqualTo(1);
    }

    [Test]
    public async Task FlushOnShutdown_StoppingAsync_PersistsLatestState()
    {
        var store = new EntityStore<TestEntity>([]);
        var snapshotStore = new FakeSnapshotStore { Stored = null };
        var persister = Create(store, snapshotStore, new FakeTimeProvider());

        await persister.StartingAsync(CancellationToken.None);
        await persister.StartedAsync(CancellationToken.None);
        store.Set(NewEntity("shutdown"));

        await persister.StoppingAsync(CancellationToken.None);

        var persisted = JsonSerializer.Deserialize<List<TestEntity>>(snapshotStore.Stored!);
        await Assert.That(persisted!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OptIn_EntityStoreWithoutPersister_BehavesAsPlainStore()
    {
        var store = new EntityStore<TestEntity>([]);
        var entity = NewEntity("a");

        store.Set(entity);

        await Assert.That(store.Contains(entity.Id)).IsTrue();
        await Assert.That(store.List().Count).IsEqualTo(1);
        await Assert.That(store.Delete(entity.Id).Succeeded).IsTrue();
        await Assert.That(store.List().Count).IsEqualTo(0);
    }
}
