using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>
/// Sessions stored before a repository kept its folder and branch (M5e, 2026-10-12) have repos of
/// only a URL and a ref, as do deleted sessions' worktrees still to be removed. Bringing the
/// database up to date names them once, by <see cref="RepoNames.Record"/> (the rules every new
/// session's repos are named by), and stores them. A row that can't be named (a URL that gives no
/// folder, which creation refuses: a corrupt row) is logged and left as it is, so the server still
/// starts: the API leaves its unnamed repos out, and the checkout refuses them (the session fails).
/// </summary>
public static class StoredRepoNames
{
    /// <returns>How many sessions and worktree removals were filled in.</returns>
    /// <remarks>Runs at every startup; it reads only ids and repos, and writes only what lacks them.</remarks>
    public static async Task<int> FillAsync(ArmDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        var filled = 0;
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.Repos != null)
            .Select(s => new { s.Id, s.Repos }).ToListAsync(cancellationToken);
        foreach (var session in sessions.Where(s => s.Repos!.Any(Unnamed)))
        {
            if (Named(session.Id, session.Repos!, logger, "Session") is not { } named)
            {
                continue;
            }

            await db.Sessions.Where(s => s.Id == session.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Repos, named), cancellationToken);
            filled++;
        }

        var removals = await db.WorktreeRemovals.AsNoTracking()
            .Select(r => new { r.SessionId, r.Repos }).ToListAsync(cancellationToken);
        foreach (var removal in removals.Where(r => r.Repos.Any(Unnamed)))
        {
            if (Named(removal.SessionId, removal.Repos, logger, "Worktree removal of session") is not { } named)
            {
                continue;
            }

            await db.WorktreeRemovals.Where(r => r.SessionId == removal.SessionId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Repos, named), cancellationToken);
            filled++;
        }

        return filled;
    }

    // Read from JSON written before the fields existed, they are null despite their type.
    private static bool Unnamed(RepoRecord repo) => repo.Folder is null || repo.Branch is null;

    /// <summary>The repos, each named; null (logged) if one can't be.</summary>
    private static IReadOnlyList<RepoRecord>? Named(Guid sessionId, IReadOnlyList<RepoRecord> repos, ILogger logger, string what)
    {
        try
        {
            return [.. repos.Select(r => Unnamed(r) ? RepoNames.Record(sessionId, r.Url, r.Ref) : r)];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError("{What} {SessionId}: its repos can't be given a folder and branch, so they are left as stored: {Problem}",
                what, sessionId, exception.Message);
            return null;
        }
    }
}
