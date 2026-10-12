using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Checks out a session's repositories (M5e) before its agent starts, as the agent's user: one
/// shared bare copy per repository (cloned on first use, fetched whenever a session that needs a
/// new worktree starts) and a worktree per session on a branch of its own. A worktree that's
/// already there (a session that continues, a retry, a resume) is left exactly as it is.
/// </summary>
/// <remarks>
/// Every session has a supervisor of its own, so the shared copy is guarded by a lock file next to
/// it (<c>&lt;copy&gt;.lock</c>, an advisory lock the kernel drops with its holder): one clone,
/// fetch or new worktree per repository at a time. A fetch only moves <c>refs/remotes/origin/*</c>
/// and tags, never a branch a worktree has checked out, so it's safe while other agents work.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class RepoCheckout(Git git, SupervisorLog log)
{
    private const int ErrorLength = 2_000;

    /// <summary>How long a session waits for another one's clone or fetch of the same repository.</summary>
    private TimeSpan LockWait => git.Timeout * 4;

    /// <summary>Checks out <paramref name="repo"/>; the error (git's own, where git said why) if that failed.</summary>
    public async Task<string?> CheckoutAsync(SupervisorRepo repo)
    {
        if (Posix.ExistsNoFollow(repo.Path))
        {
            log.Write($"{repo.Url}: {repo.Path} is there already; left as it is");
            return null;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(repo.Shared)!);
        using var held = await LockAsync(repo.Shared + ".lock");
        if (held is null)
        {
            return $"Checking out {repo.Url} failed: another session's checkout of it took more than {LockWait.TotalSeconds:0} s.";
        }

        return await UpdateAsync(repo) ?? await AddWorktreeAsync(repo);
    }

    /// <summary>Clones the shared copy if it's new (into a temporary folder, so a failed clone leaves nothing), else fetches.</summary>
    private async Task<string?> UpdateAsync(SupervisorRepo repo)
    {
        if (Directory.Exists(repo.Shared))
        {
            log.Write($"{repo.Url}: fetching into {repo.Shared}");
            return Failed(repo, await git.RunAsync("-C", repo.Shared, "fetch", "--quiet", "--prune", "--tags", "--force", "origin"), "git fetch")
                ?? await DefaultBranchAsync(repo, repo.Shared);
        }

        log.Write($"{repo.Url}: cloning into {repo.Shared}");
        var temp = $"{repo.Shared}.new";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        var error = Failed(repo, await git.RunAsync("init", "--quiet", "--bare", temp), "git init")
            ?? Failed(repo, await git.RunAsync("-C", temp, "remote", "add", "origin", repo.Url), "git remote add")
            ?? Failed(repo, await git.RunAsync("-C", temp, "fetch", "--quiet", "--prune", "--tags", "--force", "origin"), "git fetch")
            ?? await DefaultBranchAsync(repo, temp);
        if (error is not null)
        {
            Directory.Delete(temp, recursive: true);
            return error;
        }

        Directory.Move(temp, repo.Shared);
        return null;
    }

    /// <summary>Records the remote's default branch (<c>origin/HEAD</c>), when it's what the session starts from.</summary>
    private async Task<string?> DefaultBranchAsync(SupervisorRepo repo, string shared) =>
        repo.Ref is not null ? null : Failed(repo, await git.RunAsync("-C", shared, "remote", "set-head", "origin", "--auto"), "git remote set-head");

    /// <summary>
    /// The session's worktree on its own branch: new from the start point, or, if the branch exists
    /// already (its worktree was removed and it continues), that branch again.
    /// </summary>
    private async Task<string?> AddWorktreeAsync(SupervisorRepo repo)
    {
        // Worktrees whose folders are gone, so that branch isn't "checked out" anywhere any more.
        if (Failed(repo, await git.RunAsync("-C", repo.Shared, "worktree", "prune"), "git worktree prune") is { } pruneError)
        {
            return pruneError;
        }

        var branchExists = (await git.RunAsync("-C", repo.Shared, "show-ref", "--verify", "--quiet", $"refs/heads/{repo.Branch}")).Succeeded;
        if (branchExists)
        {
            log.Write($"{repo.Url}: its branch {repo.Branch} exists already; checking it out again at {repo.Path}");
            return Failed(repo, await git.RunAsync("-C", repo.Shared, "worktree", "add", "--quiet", repo.Path, repo.Branch), "git worktree add");
        }

        var (start, startError) = await StartAsync(repo);
        if (startError is not null)
        {
            return startError;
        }

        log.Write($"{repo.Url}: worktree {repo.Path} on {repo.Branch} from {start}");
        return Failed(repo, await git.RunAsync("-C", repo.Shared, "worktree", "add", "--quiet", "-b", repo.Branch, repo.Path, start!), "git worktree add");
    }

    /// <summary>The commit to start from: the remote's default branch, or the ref as a branch of the remote, a tag, or a commit.</summary>
    private async Task<(string? Commit, string? Error)> StartAsync(SupervisorRepo repo)
    {
        string[] candidates = repo.Ref is { } name ? [$"refs/remotes/origin/{name}", $"refs/tags/{name}", name] : ["refs/remotes/origin/HEAD"];
        GitResult last = default;
        foreach (var candidate in candidates)
        {
            last = await git.RunAsync("-C", repo.Shared, "rev-parse", "--verify", "--quiet", $"{candidate}^{{commit}}");
            if (last.Succeeded && last.Output.Length > 0)
            {
                return (last.Output, null);
            }

            if (last.TimedOut)
            {
                return (null, Failed(repo, last, "git rev-parse"));
            }
        }

        return (null, repo.Ref is { } unknown
            ? $"Checking out {repo.Url} failed: '{unknown}' is not a branch, tag or commit of it."
            : $"Checking out {repo.Url} failed: it has no default branch (an empty repository?).");
    }

    /// <summary>Null if <paramref name="result"/> succeeded, else the session's error: git's own message.</summary>
    private string? Failed(SupervisorRepo repo, GitResult result, string what)
    {
        if (result.Succeeded)
        {
            return null;
        }

        var error = result.TimedOut
            ? $"Checking out {repo.Url} failed: {what} took more than {git.Timeout.TotalSeconds:0} s."
            : $"Checking out {repo.Url} failed: {(result.Error.Length > 0 ? result.Error : $"{what} exited with {result.ExitCode}.")}";
        error = error.Length > ErrorLength ? error[..ErrorLength] : error;
        log.Write(error);
        return error;
    }

    /// <summary>
    /// Takes the lock file (<c>flock</c>, through .NET's exclusive open), polling until
    /// <see cref="LockWait"/> has passed; null if it never came free.
    /// </summary>
    private async Task<FileStream?> LockAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow + LockWait;
        var waited = false;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                if (!waited)
                {
                    log.Write($"waiting for {path}: another session is checking out the same repository");
                    waited = true;
                }

                await Task.Delay(100);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
