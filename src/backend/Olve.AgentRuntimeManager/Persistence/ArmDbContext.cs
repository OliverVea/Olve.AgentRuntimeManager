using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Skills;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>ARM's database (OPEN-QUESTIONS A5): SQLite for now; the schema lives in <c>Migrations/</c>.</summary>
public sealed class ArmDbContext(DbContextOptions<ArmDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<EnvVariableRecord> EnvVariables => Set<EnvVariableRecord>();

    public DbSet<WorktreeRemovalRecord> WorktreeRemovals => Set<WorktreeRemovalRecord>();

    public DbSet<SkillRecord> Skills => Set<SkillRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SessionRecordConfiguration());
        modelBuilder.ApplyConfiguration(new EnvVariableRecordConfiguration());
        modelBuilder.ApplyConfiguration(new WorktreeRemovalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SkillRecordConfiguration());
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't order or compare DateTimeOffset; UTC DateTime (ISO text) it can.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
