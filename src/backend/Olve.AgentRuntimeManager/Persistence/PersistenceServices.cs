using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>
/// The database (connection string <c>ConnectionStrings:Arm</c>) and the stores on it. Without one,
/// a SQLite file in the user's local data folder, so a local run never writes into the repo.
/// </summary>
public static class PersistenceServices
{
    public const string ConnectionStringName = "Arm";

    public static void AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName) ?? DefaultConnectionString();
        services.AddDbContextFactory<ArmDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<ISessionStore, EfSessionStore>();
    }

    /// <summary>Brings the schema up to date; run before the app serves requests.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<ArmDbContext>>().CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        // Readers don't wait for the writer (a setting of the file, so once is enough).
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    private static string DefaultConnectionString()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm");
        Directory.CreateDirectory(folder);
        return $"Data Source={Path.Combine(folder, "arm.db")}";
    }
}
