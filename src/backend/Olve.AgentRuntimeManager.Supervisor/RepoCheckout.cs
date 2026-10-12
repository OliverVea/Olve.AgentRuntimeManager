using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Checks out a session's repositories (M5e) before its agent starts, as the agent's user: one
/// shared bare copy per repository (cloned on first use, fetched whenever a session that needs a
/// new worktree starts) and a worktree per session on a branch of its own. A worktree that's
/// already there (a session that continues, a retry, a resume) is left exactly as it is.
/// </summary>
/// <remarks>
/// <para>
/// Every session has a supervisor of its own, so the shared copy is guarded by a lock file next to
/// it (<see cref="RepoLock"/>): one clone, fetch or worktree change per repository at a time, and
/// every look at a worktree is made holding it. A fetch only moves <c>refs/remotes/origin/*</c>
/// and tags, never a branch a worktree has checked out, so it's safe while other agents work.
/// </para>
/// <para>
/// A worktree only ever appears whole: it's made at <see cref="SupervisorRepo.PendingPath"/> and
/// moved into place once git is done. One left half-made (a kill, a timeout, a crash) is removed
/// on the next attempt; a kill or failure removes it, and a branch it had just made, at once.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed partial class RepoCheckout(Git git, SupervisorLog log)
{
    private const int ErrorLength = 2_000;

    /// <summary>Cleaning up after a kill or failure isn't stopped by the kill, but is bounded.</summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a session waits for another one's checkout of the same repository.</summary>
    private TimeSpan LockWait => git.Timeout * 4;

    /// <summary>
    /// A git lock file in a shared copy older than this is left by a git that died (ARM's own git
    /// commands take at most a git timeout, and an agent's hold their locks for moments).
    /// </summary>
    private TimeSpan StaleLockAge => git.Timeout * 2 > TimeSpan.FromMinutes(1) ? git.Timeout * 2 : TimeSpan.FromMinutes(1);

    /// <summary>Checks out <paramref name="repo"/>; the error (git's own, where git said why) if that failed.</summary>
    public async Task<string?> CheckoutAsync(SupervisorRepo repo)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(repo.Shared)!);
        Microsoft.Win32.SafeHandles.SafeFileHandle? held;
        try
        {
            held = await RepoLock.AcquireAsync(repo.Shared + ".lock", LockWait, git.Cancel,
                () => log.Write($"{repo.Url}: waiting for {repo.Shared}.lock: another session is checking it out"));
        }
        catch (OperationCanceledException)
        {
            return Stopped(repo);
        }
        catch (IOException exception)
        {
            return Error(repo, $"Checking out {repo.Url} failed: {exception.Message}");
        }

        if (held is null)
        {
            return Error(repo, $"Checking out {repo.Url} failed: another session's checkout of it took more than {LockWait.TotalSeconds:0} s.");
        }

        using (held)
        {
            RemoveStaleGitLocks(repo.Shared);
            if (await ExistingAsync(repo) is { } existing)
            {
                return existing.Length == 0 ? null : existing;
            }

            if (await CleanUpAsync(repo, createdBranch: false) is { } leftover)
            {
                return Error(repo, $"Checking out {repo.Url} failed: a half-made worktree from before couldn't be removed: {leftover}");
            }

            return await UpdateAsync(repo) ?? await AddWorktreeAsync(repo);
        }
    }

    /// <summary>
    /// The worktree that's there already: "" if it's a whole one (kept as it is, nothing fetched),
    /// an error if something else is in its place, null if there's none.
    /// </summary>
    private async Task<string?> ExistingAsync(SupervisorRepo repo)
    {
        if (Directory.Exists(repo.Shared))
        {
            // Worktrees whose folders are gone, so their branches aren't "checked out" anywhere any more.
            if (Failed(repo, await git.RunAsync("-C", repo.Shared, "worktree", "prune"), "git worktree prune") is { } pruneError)
            {
                return pruneError;
            }
        }

        if (!Posix.ExistsNoFollow(repo.Path))
        {
            return null;
        }

        if (Directory.Exists(repo.Shared) && await Worktrees.IsRegisteredAsync(git, repo.Shared, repo.Path))
        {
            log.Write($"{repo.Url}: {repo.Path} is there already; left as it is");
            return "";
        }

        return Error(repo, $"Checking out {repo.Url} failed: {repo.Path} is there already but isn't a worktree of its shared copy.");
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
    /// The session's worktree on its own branch: a new branch from the start point, or the branch it
    /// had (its worktree was removed and it continues). A branch of that name that's another
    /// session's (an id sharing its first 8 characters) is an error, not taken over.
    /// </summary>
    private async Task<string?> AddWorktreeAsync(SupervisorRepo repo)
    {
        var ownerKey = SupervisorRepo.OwnerKey(repo.Branch);
        var createdBranch = false;
        if ((await git.RunAsync("-C", repo.Shared, "show-ref", "--verify", "--quiet", $"refs/heads/{repo.Branch}")).Succeeded)
        {
            var owner = await git.RunAsync("-C", repo.Shared, "config", "--get", ownerKey);
            if (owner.Output != repo.Owner)
            {
                return Error(repo, $"Checking out {repo.Url} failed: its shared copy has a branch {repo.Branch} already, and it isn't this session's.");
            }

            log.Write($"{repo.Url}: this session's branch {repo.Branch} exists; checking it out again");
        }
        else
        {
            var (start, startError) = await StartAsync(repo);
            if (startError is not null)
            {
                return startError;
            }

            // The owner first: a branch is never there without it (one left by a crash is taken back).
            createdBranch = true;
            var made = Failed(repo, await git.RunAsync("-C", repo.Shared, "config", ownerKey, repo.Owner), "git config")
                ?? Failed(repo, await git.RunAsync("-C", repo.Shared, "branch", "--no-track", repo.Branch, start!), "git branch");
            if (made is not null)
            {
                await CleanUpAsync(repo, createdBranch);
                return made;
            }

            log.Write($"{repo.Url}: branch {repo.Branch} from {start}");
        }

        var pending = SupervisorRepo.PendingPath(repo.Path);
        var error = Failed(repo, await git.RunAsync("-C", repo.Shared, "worktree", "add", "--quiet", pending, repo.Branch), "git worktree add")
            ?? Failed(repo, await git.RunAsync("-C", repo.Shared, "worktree", "move", pending, repo.Path), "git worktree move");
        if (error is not null)
        {
            await CleanUpAsync(repo, createdBranch);
            return error;
        }

        log.Write($"{repo.Url}: worktree {repo.Path} on {repo.Branch}");
        return null;
    }

    /// <summary>
    /// Removes a half-made worktree (at its pending path), and the branch if this attempt made it;
    /// not stopped by a kill (that's what it cleans up after). The error, if something stayed.
    /// </summary>
    private async Task<string?> CleanUpAsync(SupervisorRepo repo, bool createdBranch)
    {
        var pending = SupervisorRepo.PendingPath(repo.Path);
        if (!Directory.Exists(repo.Shared))
        {
            return Posix.ExistsNoFollow(pending) ? $"{pending} is there, and no shared copy" : null;
        }

        var cleanup = git.Uncancellable(CleanupTimeout);
        string? error = null;
        if (Posix.ExistsNoFollow(pending) || await Worktrees.IsRegisteredAsync(cleanup, repo.Shared, pending))
        {
            log.Write($"{repo.Url}: removing the half-made worktree {pending}");
            error = await Worktrees.RemoveAsync(cleanup, repo.Shared, pending);
        }

        if (createdBranch)
        {
            await cleanup.RunAsync("-C", repo.Shared, "branch", "-D", repo.Branch);
            await cleanup.RunAsync("-C", repo.Shared, "config", "--unset", SupervisorRepo.OwnerKey(repo.Branch));
        }

        return error;
    }

    /// <summary>
    /// The commit to start from: the remote's default branch, or the ref as one of the remote's
    /// branches, a tag, or a commit id (whole or abbreviated). Never a branch of the shared copy's
    /// own: those are other sessions' (<c>arm/…</c>).
    /// </summary>
    private async Task<(string? Commit, string? Error)> StartAsync(SupervisorRepo repo)
    {
        if (repo.Ref is not { } name)
        {
            return await CommitOfAsync(repo, "refs/remotes/origin/HEAD") is { } head
                ? (head, null)
                : (null, git.Cancel.IsCancellationRequested ? Stopped(repo) : Error(repo, $"Checking out {repo.Url} failed: it has no default branch (an empty repository?)."));
        }

        foreach (var candidate in (string[])[$"refs/remotes/origin/{name}", $"refs/tags/{name}"])
        {
            if (await CommitOfAsync(repo, candidate) is { } commit)
            {
                return (commit, null);
            }
        }

        if (CommitId().IsMatch(name))
        {
            // Objects only (a ref named like it doesn't count): every object the id may abbreviate, then the commits among them.
            var objects = await git.RunAsync("-C", repo.Shared, "rev-parse", $"--disambiguate={name.ToLowerInvariant()}");
            var commits = new List<string>();
            foreach (var id in objects.Succeeded ? objects.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
            {
                if ((await git.RunAsync("-C", repo.Shared, "cat-file", "-t", id)).Output == "commit")
                {
                    commits.Add(id);
                }
            }

            if (commits is [var only])
            {
                return (only, null);
            }

            if (commits.Count > 1)
            {
                return (null, Error(repo, $"Checking out {repo.Url} failed: '{name}' is the start of {commits.Count} commits; give more of it."));
            }
        }

        return (null, git.Cancel.IsCancellationRequested ? Stopped(repo)
            : Error(repo, $"Checking out {repo.Url} failed: '{name}' is not a branch, tag or commit of it."));
    }

    /// <summary>The commit <paramref name="reference"/> names (peeled), or null.</summary>
    private async Task<string?> CommitOfAsync(SupervisorRepo repo, string reference)
    {
        var result = await git.RunAsync("-C", repo.Shared, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}");
        return result.Succeeded && result.Output.Length > 0 ? result.Output : null;
    }

    /// <summary>
    /// Git lock files a git that died left in the shared copy (a crash or SIGKILL mid-fetch); they'd
    /// fail every later fetch. Only old ones: an agent's git may hold one right now (its worktrees'
    /// own, under <c>worktrees/</c>, aren't touched).
    /// </summary>
    private void RemoveStaleGitLocks(string shared)
    {
        if (!Directory.Exists(shared))
        {
            return;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var worktrees = System.IO.Path.Combine(shared, "worktrees") + System.IO.Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(shared, "*.lock", options))
        {
            if (file.StartsWith(worktrees, StringComparison.Ordinal) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < StaleLockAge)
            {
                continue;
            }

            log.Write($"removing the stale git lock {file}");
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.Write($"could not remove {file}: {exception.Message}");
            }
        }
    }

    /// <summary>Null if <paramref name="result"/> succeeded, else the session's error: git's own message.</summary>
    private string? Failed(SupervisorRepo repo, GitResult result, string what) =>
        result.Succeeded ? null
        : result.Cancelled ? Stopped(repo)
        : result.TimedOut ? Error(repo, $"Checking out {repo.Url} failed: {what} took more than {git.Timeout.TotalSeconds:0} s.")
        : Error(repo, $"Checking out {repo.Url} failed: {(result.Error.Length > 0 ? result.Error : $"{what} exited with {result.ExitCode}.")}");

    private string Stopped(SupervisorRepo repo) => Error(repo, $"Checking out {repo.Url} was stopped.");

    private string Error(SupervisorRepo repo, string error)
    {
        error = error.Length > ErrorLength ? error[..ErrorLength] : error;
        log.Write(error);
        return error;
    }

    [GeneratedRegex("^[0-9a-fA-F]{4,64}$")]
    private static partial Regex CommitId();
}
