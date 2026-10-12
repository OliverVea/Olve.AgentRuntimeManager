using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary><see cref="IWorktreeRemovalStore"/> on EF Core: a short-lived context per call.</summary>
public sealed class EfWorktreeRemovalStore(IDbContextFactory<ArmDbContext> contexts) : IWorktreeRemovalStore
{
    public void Save(WorktreeRemovalRecord removal)
    {
        using var db = contexts.CreateDbContext();
        if (db.WorktreeRemovals.AsNoTracking().Any(r => r.SessionId == removal.SessionId))
        {
            db.WorktreeRemovals.Update(removal);
        }
        else
        {
            db.WorktreeRemovals.Add(removal);
        }

        db.SaveChanges();
    }

    public void Delete(Guid sessionId)
    {
        using var db = contexts.CreateDbContext();
        db.WorktreeRemovals.Where(r => r.SessionId == sessionId).ExecuteDelete();
    }

    public IReadOnlyList<WorktreeRemovalRecord> Due(DateTimeOffset now)
    {
        using var db = contexts.CreateDbContext();
        return [.. db.WorktreeRemovals.AsNoTracking().Where(r => r.NextAttemptAt <= now).OrderBy(r => r.NextAttemptAt)];
    }
}
