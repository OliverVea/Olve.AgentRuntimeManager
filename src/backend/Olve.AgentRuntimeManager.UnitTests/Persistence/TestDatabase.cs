using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Olve.AgentRuntimeManager.Persistence;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.UnitTests.Persistence;

/// <summary>
/// A migrated SQLite file of its own, deleted on dispose. A file, not <c>:memory:</c>: every
/// context opens its own connection, like the app's, and they must all see the same database.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("arm-db-").FullName;
    private readonly DbContextOptions<ArmDbContext> _options;

    public TestDatabase()
    {
        _options = new DbContextOptionsBuilder<ArmDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        using var db = new ArmDbContext(_options);
        db.Database.Migrate();
    }

    public string ConnectionString => $"Data Source={Path.Combine(_folder, "arm.db")};Pooling=False";

    /// <summary>A context on this database, to read or write it as no store does.</summary>
    public ArmDbContext Context() => new(_options);

    /// <summary>A new store on this database, as a restarted server would open it.</summary>
    public ISessionStore Store() => new EfSessionStore(new PooledDbContextFactory<ArmDbContext>(_options));

    /// <summary>The deleted sessions' worktrees still to be removed, on this database.</summary>
    public IWorktreeRemovalStore Removals() => new EfWorktreeRemovalStore(new PooledDbContextFactory<ArmDbContext>(_options));

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
