using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>The <c>Sessions</c> table: one row per session, enums by name.</summary>
public sealed class SessionRecordConfiguration : IEntityTypeConfiguration<SessionRecord>
{
    public void Configure(EntityTypeBuilder<SessionRecord> session)
    {
        session.ToTable("Sessions");
        session.HasKey(s => s.Id);
        // Derived from the queue (FIFO by creation), not stored.
        session.Ignore(s => s.QueuePosition);
        session.Property(s => s.Status).HasConversion<string>();
        session.Property(s => s.KillSource).HasConversion<string>();
        session.HasIndex(s => s.CreatedAt);
        session.HasIndex(s => s.Status);
    }
}
