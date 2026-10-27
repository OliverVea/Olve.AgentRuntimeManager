using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Olve.AgentRuntimeManager.Sessions.Skills;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>The <c>Skills</c> table: one row per registered skill (M10b).</summary>
public sealed class SkillRecordConfiguration : IEntityTypeConfiguration<SkillRecord>
{
    public void Configure(EntityTypeBuilder<SkillRecord> skill)
    {
        skill.ToTable("Skills");
        skill.HasKey(s => s.Name);
    }
}
