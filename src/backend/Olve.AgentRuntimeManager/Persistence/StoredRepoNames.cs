using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>
/// Sessions stored before a repository kept its folder and branch (M5e, 2026-10-12) have repos of
/// only a URL and a ref, as do deleted sessions' worktrees still to be removed. Bringing the
/// database up to date names them once, by <see cref="RepoNames.Record"/> (the rules every new
/// session's repos are named by), and stores them; nothing reads a repo without them afterwards.
/// </summary>
public static class StoredRepoNames
{
    /// <returns>How many sessions and worktree removals were filled in.</returns>
    /// <remarks>Runs at every startup; it reads only ids and repos, and writes only what lacks them.</remarks>
    public static async Task<int> FillAsync(ArmDbContext db, CancellationToken cancellationToken = default)
    {
        var filled = 0;
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.Repos != null)
            .Select(s => new { s.Id, s.Repos }).ToListAsync(cancellationToken);
        foreach (var session in sessions.Where(s => s.Repos!.Any(Unnamed)))
        {
            var named = Named(session.Id, session.Repos!);
            await db.Sessions.Where(s => s.Id == session.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Repos, named), cancellationToken);
            filled++;
        }

        var removals = await db.WorktreeRemovals.AsNoTracking()
            .Select(r => new { r.SessionId, r.Repos }).ToListAsync(cancellationToken);
        foreach (var removal in removals.Where(r => r.Repos.Any(Unnamed)))
        {
            var named = Named(removal.SessionId, removal.Repos);
            await db.WorktreeRemovals.Where(r => r.SessionId == removal.SessionId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Repos, named), cancellationToken);
            filled++;
        }

        return filled;
    }

    // Read from JSON written before the fields existed, they are null despite their type.
    private static bool Unnamed(RepoRecord repo) => repo.Folder is null || repo.Branch is null;

    private static IReadOnlyList<RepoRecord> Named(Guid sessionId, IReadOnlyList<RepoRecord> repos) =>
        [.. repos.Select(r => Unnamed(r) ? RepoNames.Record(sessionId, r.Url, r.Ref) : r)];
}
