using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>A worktree-removal store whose saves fail (the database refusing a write), until a test says otherwise.</summary>
public sealed class FailingRemovalStore(IWorktreeRemovalStore inner) : IWorktreeRemovalStore
{
    public bool Fails { get; set; } = true;

    public void Save(WorktreeRemovalRecord removal)
    {
        if (Fails)
        {
            throw new InvalidOperationException("The database refused the write.");
        }

        inner.Save(removal);
    }

    public void Delete(Guid sessionId) => inner.Delete(sessionId);

    public IReadOnlyList<WorktreeRemovalRecord> Due(DateTimeOffset now) => inner.Due(now);
}
