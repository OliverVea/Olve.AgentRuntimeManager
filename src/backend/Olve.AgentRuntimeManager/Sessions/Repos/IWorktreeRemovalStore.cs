namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>Worktrees of deleted sessions still to be removed (<see cref="WorktreeRemovalRecord"/>).</summary>
public interface IWorktreeRemovalStore
{
    /// <summary>Adds or replaces the session's entry.</summary>
    void Save(WorktreeRemovalRecord removal);

    void Delete(Guid sessionId);

    /// <summary>The entries due at <paramref name="now"/>, earliest first.</summary>
    IReadOnlyList<WorktreeRemovalRecord> Due(DateTimeOffset now);
}
