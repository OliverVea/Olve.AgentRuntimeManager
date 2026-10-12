namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>
/// The worktrees of a deleted session that couldn't be removed yet (M5e: its repository was busy,
/// git failed), kept until a retry removes them, so a delete never leaves one behind unnoticed.
/// </summary>
public sealed record WorktreeRemovalRecord
{
    /// <summary>The deleted session (its record is gone).</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The provider whose workplace the worktrees are in.</summary>
    public required string Provider { get; init; }

    /// <summary>The repositories whose worktrees are still there.</summary>
    public required IReadOnlyList<RepoRecord> Repos { get; init; }

    /// <summary>How often removing them failed.</summary>
    public required int Attempts { get; init; }

    public required DateTimeOffset NextAttemptAt { get; init; }
}
