using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary><see cref="ISessionStore"/> on EF Core: a short-lived context per call.</summary>
public sealed class EfSessionStore(IDbContextFactory<ArmDbContext> contexts) : ISessionStore
{
    public void Add(SessionRecord session)
    {
        using var db = contexts.CreateDbContext();
        db.Sessions.Add(session);
        db.SaveChanges();
    }

    public void Update(SessionRecord session)
    {
        using var db = contexts.CreateDbContext();
        db.Sessions.Update(session);
        db.SaveChanges();
    }

    public void Delete(Guid id)
    {
        using var db = contexts.CreateDbContext();
        db.Sessions.Where(s => s.Id == id).ExecuteDelete();
    }

    public SessionRecord? Get(Guid id)
    {
        using var db = contexts.CreateDbContext();
        return db.Sessions.AsNoTracking().SingleOrDefault(s => s.Id == id);
    }

    public IReadOnlyList<SessionRecord> Active()
    {
        using var db = contexts.CreateDbContext();
        return db.Sessions.AsNoTracking()
            .Where(s => s.Status == SessionStatus.Queued || s.Status == SessionStatus.Working)
            .OrderBy(s => s.CreatedAt)
            .ToList();
    }

    public SessionQueryResult Search(SessionSearch search, int limit, int offset)
    {
        using var db = contexts.CreateDbContext();
        var query = db.Sessions.AsNoTracking();
        if (search.Status is { } statuses)
        {
            query = query.Where(s => statuses.Contains(s.Status));
        }

        if (search.Caller is { } caller)
        {
            query = query.Where(s => s.Caller == caller);
        }

        if (search.CreatedAfter is { } after)
        {
            query = query.Where(s => s.CreatedAt > after);
        }

        if (search.CreatedBefore is { } before)
        {
            query = query.Where(s => s.CreatedAt < before);
        }

        var total = query.Count();
        var items = query
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Skip(offset)
            .Take(limit)
            .ToList();
        return new SessionQueryResult(items, total, limit, offset);
    }
}
