using Olve.AgentRuntimeManager.Stores;

namespace Olve.AgentRuntimeManager.UnitTests.Stores;

/// <summary>An in-memory <see cref="ISnapshotStore"/> with hooks for the persister's safety tests.</summary>
public sealed class FakeSnapshotStore : ISnapshotStore
{
    public byte[]? Stored { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }

    /// <summary>When set, replaces the read behaviour (e.g. to throw a transient failure).</summary>
    public Func<byte[]?>? ReadOverride { get; set; }

    public Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(ReadOverride is not null ? ReadOverride() : Stored);
    }

    public Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        Writes++;
        Stored = content;
        return Task.CompletedTask;
    }
}
