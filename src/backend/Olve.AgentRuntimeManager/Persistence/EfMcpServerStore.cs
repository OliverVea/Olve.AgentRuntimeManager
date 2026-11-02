using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.McpServers;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary><see cref="IMcpServerStore"/> on EF Core: a short-lived context per call.</summary>
public sealed class EfMcpServerStore(IDbContextFactory<ArmDbContext> contexts) : IMcpServerStore
{
    public IReadOnlyList<McpServerRecord> List()
    {
        using var db = contexts.CreateDbContext();
        return db.McpServers.AsNoTracking().OrderBy(s => s.Name).ToList();
    }

    public McpServerRecord? Find(string name)
    {
        using var db = contexts.CreateDbContext();
        return db.McpServers.AsNoTracking().SingleOrDefault(s => s.Name == name);
    }

    public void Set(McpServerRecord server)
    {
        using var db = contexts.CreateDbContext();
        if (db.McpServers.Any(s => s.Name == server.Name))
        {
            db.McpServers.Update(server);
        }
        else
        {
            db.McpServers.Add(server);
        }

        db.SaveChanges();
    }

    public bool Delete(string name)
    {
        using var db = contexts.CreateDbContext();
        return db.McpServers.Where(s => s.Name == name).ExecuteDelete() > 0;
    }
}
