using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>Removing a deleted session's worktree from the shared copy of its repository (M5e).</summary>
[UnsupportedOSPlatform("windows")]
internal static class RepoWorktree
{
    /// <summary>All of it, the wait for the lock included: a delete request waits for this.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Removes the worktree at <paramref name="worktree"/> (and a half-made one at its pending path),
    /// uncommitted work and all; the branch stays in the shared copy. Holds the copy's lock, so it
    /// doesn't meet a checkout. Null when done (or there was nothing to remove), else why not.
    /// </summary>
    public static async Task<string?> RemoveAsync(string shared, string worktree)
    {
        if (!Directory.Exists(shared))
        {
            return Posix.ExistsNoFollow(worktree) ? $"{worktree} has no shared copy at {shared} to be removed from." : null;
        }

        using var deadline = new CancellationTokenSource(Timeout);
        Microsoft.Win32.SafeHandles.SafeFileHandle? held;
        try
        {
            held = await RepoLock.AcquireAsync(shared + ".lock", LockWait, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            held = null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }

        if (held is null)
        {
            return $"{shared} is busy (another session's checkout of it took more than {LockWait.TotalSeconds:0} s).";
        }

        using (held)
        {
            // Nothing here needs the network or the agent's own settings: a plain environment.
            var git = new Git(new Dictionary<string, string> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin" }, Timeout, deadline.Token);
            return await Worktrees.RemoveAsync(git, shared, SupervisorRepo.PendingPath(worktree))
                ?? await Worktrees.RemoveAsync(git, shared, worktree);
        }
    }
}
