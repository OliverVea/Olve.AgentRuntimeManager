using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>A shared copy's worktrees (M5e): which it knows, and removing one whatever state it's in.</summary>
[UnsupportedOSPlatform("windows")]
internal static class Worktrees
{
    /// <summary>Whether <paramref name="path"/> is a worktree <paramref name="shared"/> has (<c>git worktree list</c>).</summary>
    public static async Task<bool> IsRegisteredAsync(Git git, string shared, string path)
    {
        var list = await git.RunAsync("-C", shared, "worktree", "list", "--porcelain");
        var full = Path.GetFullPath(path);
        return list.Succeeded && list.Output.Split('\n').Any(line => line == $"worktree {full}");
    }

    /// <summary>
    /// Removes the worktree at <paramref name="path"/>, uncommitted work and all (even if it's locked).
    /// Where git refuses (a worktree with initialised submodules, a half-made one), the folder is
    /// deleted and the copy pruned instead. Null when nothing is left of it, else why not.
    /// </summary>
    public static async Task<string?> RemoveAsync(Git git, string shared, string path)
    {
        if (Posix.ExistsNoFollow(path))
        {
            var removed = await git.RunAsync("-C", shared, "worktree", "remove", "--force", "--force", path);
            if (!removed.Succeeded && Posix.ExistsNoFollow(path))
            {
                try
                {
                    // Not into links: a link in it is removed, not what it points to.
                    if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return $"could not delete {path}: {exception.Message}";
                }
            }
        }

        var pruned = await git.RunAsync("-C", shared, "worktree", "prune");
        return pruned.Succeeded ? null : $"git worktree prune failed: {(pruned.TimedOut ? "timed out" : pruned.Error)}";
    }
}
