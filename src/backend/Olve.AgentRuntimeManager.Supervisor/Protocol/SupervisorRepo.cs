namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// A git repository the supervisor checks out before it starts the agent (M5e): a worktree at
/// <see cref="Path"/> on a new branch <see cref="Branch"/>, from <see cref="Ref"/> (unset: the
/// remote's default branch), made from the shared bare copy at <see cref="Shared"/> (cloned if it's
/// missing, else fetched). ARM works out every path; the supervisor only runs git.
/// </summary>
public sealed record SupervisorRepo
{
    public required string Url { get; init; }

    /// <summary>A branch, tag or commit; null: the remote's default branch.</summary>
    public string? Ref { get; init; }

    /// <summary>The shared bare repository for <see cref="Url"/> (one per URL, shared by every session).</summary>
    public required string Shared { get; init; }

    /// <summary>The session's worktree: a folder in its workplace, named after the repository.</summary>
    public required string Path { get; init; }

    /// <summary>The session's branch (<c>arm/&lt;short session id&gt;</c>).</summary>
    public required string Branch { get; init; }

    /// <summary>
    /// The session it's for (its full id): recorded on the branch in the shared copy
    /// (<see cref="OwnerKey"/>), so a branch is only checked out again for the session that made it.
    /// </summary>
    public required string Owner { get; init; }

    /// <summary>
    /// Where a worktree is made before it's moved to <paramref name="path"/>, complete: next to it,
    /// hidden. What's at <paramref name="path"/> is so always a whole worktree; one left here is half-made.
    /// </summary>
    public static string PendingPath(string path) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".{System.IO.Path.GetFileName(path)}.arm-checkout");

    /// <summary>The shared copy's git config key naming the session a branch is for.</summary>
    public static string OwnerKey(string branch) => $"branch.{branch}.armSession";
}
