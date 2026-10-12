using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>Removing a deleted session's worktree from the shared copy of its repository (M5e).</summary>
[UnsupportedOSPlatform("windows")]
internal static class RepoWorktree
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Removes the worktree at <paramref name="worktree"/>, uncommitted work and all (even if it's
    /// locked); the branch stays in the shared copy. A worktree whose folder is gone is pruned instead.
    /// Null when done (or there was nothing to remove), else why not.
    /// </summary>
    public static async Task<string?> RemoveAsync(string shared, string worktree)
    {
        if (!Directory.Exists(shared))
        {
            return Posix.ExistsNoFollow(worktree) ? $"{worktree} has no shared copy at {shared} to be removed from." : null;
        }

        // Nothing here needs the network or the agent's own settings: a plain environment.
        var git = new Git(new Dictionary<string, string> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin" }, Timeout);
        if (!Posix.ExistsNoFollow(worktree))
        {
            var pruned = await git.RunAsync("-C", shared, "worktree", "prune");
            return pruned.Succeeded ? null : $"git worktree prune failed: {(pruned.TimedOut ? "timed out" : pruned.Error)}";
        }

        var removed = await git.RunAsync("-C", shared, "worktree", "remove", "--force", "--force", worktree);
        return removed.Succeeded ? null : $"git worktree remove failed: {(removed.TimedOut ? "timed out" : removed.Error)}";
    }
}
