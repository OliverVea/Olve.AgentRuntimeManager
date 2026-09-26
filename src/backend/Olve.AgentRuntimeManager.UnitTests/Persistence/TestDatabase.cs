using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Olve.AgentRuntimeManager.Persistence;
using Olve.AgentRuntimeManager.Sessions;

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
            .UseSqlite($"Data Source={Path.Combine(_folder, "arm.db")};Pooling=False")
            .Options;
        using var db = new ArmDbContext(_options);
        db.Database.Migrate();
    }

    /// <summary>A new store on this database, as a restarted server would open it.</summary>
    public ISessionStore Store() => new EfSessionStore(new PooledDbContextFactory<ArmDbContext>(_options));

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
