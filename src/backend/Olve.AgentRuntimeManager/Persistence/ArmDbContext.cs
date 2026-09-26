using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>ARM's database (OPEN-QUESTIONS A5): SQLite for now; the schema lives in <c>Migrations/</c>.</summary>
public sealed class ArmDbContext(DbContextOptions<ArmDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new SessionRecordConfiguration());

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't order or compare DateTimeOffset; UTC DateTime (ISO text) it can.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
