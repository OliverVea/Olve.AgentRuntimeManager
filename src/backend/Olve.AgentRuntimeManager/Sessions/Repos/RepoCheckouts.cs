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

    /// <summary>What the supervisor checks out for the session in <paramref name="workplace"/>.</summary>
    public IReadOnlyList<SupervisorRepo> Plan(Guid sessionId, string workplace, IReadOnlyList<RepoRecord>? repos) =>
        [.. (repos ?? []).Select(repo => new SupervisorRepo
        {
            Url = repo.Url.Trim(),
            Ref = repo.Ref,
            Shared = Path.Combine(Root(), RepoNames.Shared(repo.Url)),
            Path = Worktree(workplace, repo),
            Branch = RepoNames.Branch(sessionId),
        })];

    /// <summary>Where the agent starts: in the repository when there's exactly one (so it reads its <c>CLAUDE.md</c>), else the workplace.</summary>
    public static string AgentDirectory(string workplace, IReadOnlyList<RepoRecord>? repos) =>
        repos is [var only] ? Worktree(workplace, only) : workplace;

    /// <summary>
    /// Removes a deleted session's worktrees from the shared copies (their branches stay there).
    /// Best effort: what can't be removed is logged, and the session is deleted anyway.
    /// </summary>
    public void Remove(Guid sessionId, string workplace, IReadOnlyList<RepoRecord> repos)
    {
        foreach (var repo in Plan(sessionId, workplace, repos))
        {
            if (supervisors.RemoveWorktree(repo.Shared, repo.Path) is { } error)
            {
                logger.LogWarning("Session {SessionId}: could not remove its worktree {Worktree} of {Url}: {Error}", sessionId, repo.Path, repo.Url, error);
            }
            else
            {
                logger.LogInformation("Session {SessionId}: removed its worktree {Worktree} of {Url}", sessionId, repo.Path, repo.Url);
            }
        }
    }

    private static string Worktree(string workplace, RepoRecord repo) =>
        Path.Combine(workplace, RepoNames.Folder(repo.Url) ?? throw new InvalidOperationException($"'{repo.Url}' names no repository folder."));

    private string Root() =>
        options.Value.Root is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "repos");
}
