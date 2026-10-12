using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>The <c>WorktreeRemovals</c> table: one row per deleted session whose worktrees are still to be removed.</summary>
public sealed class WorktreeRemovalRecordConfiguration : IEntityTypeConfiguration<WorktreeRemovalRecord>
{
    public void Configure(EntityTypeBuilder<WorktreeRemovalRecord> removal)
    {
        removal.ToTable("WorktreeRemovals");
        removal.HasKey(r => r.SessionId);
        removal.Property<IReadOnlyList<RepoRecord>?>(nameof(WorktreeRemovalRecord.Repos)).HasConversion(JsonColumn.Converter<IReadOnlyList<RepoRecord>>(), JsonColumn.Comparer<IReadOnlyList<RepoRecord>>());
        removal.HasIndex(r => r.NextAttemptAt);
    }
}
