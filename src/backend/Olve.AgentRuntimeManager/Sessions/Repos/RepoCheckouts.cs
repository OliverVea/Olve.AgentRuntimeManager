using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>
/// A session's repositories on this machine (M5e): where each is checked out (a worktree in the
/// session's workplace, on the session's branch, from a shared bare copy under
/// <see cref="RepoOptions.Root"/>), and removing those worktrees when the session is deleted. The
/// checkout itself is the supervisor's, as the agent's user, before the agent starts.
/// </summary>
public sealed class RepoCheckouts(IOptions<RepoOptions> options, Supervisors supervisors, ILogger<RepoCheckouts> logger)
{
    public TimeSpan GitTimeout => options.Value.GitTimeout;

    public TimeSpan? CheckoutTimeout => options.Value.CheckoutTimeout;

    /// <summary>What the supervisor checks out (or removes) for the session in <paramref name="workplace"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// A stored folder isn't one plain folder name in the workplace, or a branch is missing (a corrupt
    /// row): nothing is checked out or removed (a start fails the session with this message).
    /// </exception>
    public IReadOnlyList<SupervisorRepo> Plan(Guid sessionId, string workplace, IReadOnlyList<RepoRecord>? repos) =>
        [.. (repos ?? []).Select(repo => new SupervisorRepo
        {
            Url = repo.Url.Trim(),
            Ref = repo.Ref,
            Shared = Path.Combine(Root(), RepoNames.Shared(repo.Url)),
            // The folder and branch the session was created with (and returns).
            Path = Worktree(workplace, repo),
            Branch = string.IsNullOrWhiteSpace(repo.Branch)
                ? throw new InvalidOperationException($"Checking out {repo.Url} failed: it has no branch stored.")
                : repo.Branch,
            Owner = sessionId.ToString(),
        })];

    /// <summary>
    /// Removes a deleted session's worktrees from the shared copies (their branches stay there).
    /// Best effort: what can't be removed is logged, and the session is deleted anyway.
    /// </summary>
    /// <remarks>All at once, each bounded (<see cref="Supervisors.RemoveTimeout"/>): a delete waits for this.</remarks>
    /// <returns>The repositories whose worktrees couldn't be removed (to be tried again).</returns>
    public IReadOnlyList<RepoRecord> Remove(Guid sessionId, string workplace, IReadOnlyList<RepoRecord> repos)
    {
        var plan = Plan(sessionId, workplace, repos);
        var removals = plan.Select(repo => Task.Run(() => supervisors.RemoveWorktree(repo.Shared, repo.Path))).ToArray();
        Task.WaitAll(removals);
        var left = new List<RepoRecord>();
        for (var i = 0; i < plan.Count; i++)
        {
            if (removals[i].Result is { } error)
            {
                logger.LogWarning("Session {SessionId}: could not remove its worktree {Worktree} of {Url}: {Error}", sessionId, plan[i].Path, plan[i].Url, error);
                left.Add(repos[i]);
            }
            else
            {
                logger.LogInformation("Session {SessionId}: removed its worktree {Worktree} of {Url}", sessionId, plan[i].Path, plan[i].Url);
            }
        }

        return left;
    }

    /// <summary>The repo's worktree: its stored folder, which must be one plain folder name directly in the workplace.</summary>
    private static string Worktree(string workplace, RepoRecord repo)
    {
        var folder = repo.Folder;
        var plain = !string.IsNullOrWhiteSpace(folder) && folder is not ("." or "..")
            && folder.IndexOfAny(['/', '\\']) < 0 && !folder.Any(char.IsControl) && !Path.IsPathRooted(folder);
        var inside = plain && Path.GetDirectoryName(Path.GetFullPath(Path.Combine(workplace, folder!)))
            == Path.GetFullPath(workplace).TrimEnd(Path.DirectorySeparatorChar);
        return inside
            ? Path.Combine(workplace, folder!)
            : throw new InvalidOperationException($"Checking out {repo.Url} failed: its stored folder '{folder}' isn't a folder name in the session's workplace.");
    }

    private string Root() =>
        options.Value.Root is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "repos");
}
