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
    public static async Task<int> FillAsync(ArmDbContext db, CancellationToken cancellationToken = default)
    {
        var filled = 0;
        foreach (var session in await db.Sessions.Where(s => s.Repos != null).ToListAsync(cancellationToken))
        {
            if (session.Repos!.Any(Unnamed))
            {
                db.Entry(session).Property(s => s.Repos).CurrentValue = Named(session.Id, session.Repos!);
                filled++;
            }
        }

        foreach (var removal in await db.WorktreeRemovals.ToListAsync(cancellationToken))
        {
            if (removal.Repos.Any(Unnamed))
            {
                db.Entry(removal).Property(r => r.Repos).CurrentValue = Named(removal.SessionId, removal.Repos);
                filled++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return filled;
    }

    // Read from JSON written before the fields existed, they are null despite their type.
    private static bool Unnamed(RepoRecord repo) => repo.Folder is null || repo.Branch is null;

    private static List<RepoRecord> Named(Guid sessionId, IReadOnlyList<RepoRecord> repos) =>
        [.. repos.Select(r => Unnamed(r) ? RepoNames.Record(sessionId, r.Url, r.Ref) : r)];
}
