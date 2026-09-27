using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>The <c>EnvVariables</c> table: one row per registered environment variable.</summary>
public sealed class EnvVariableRecordConfiguration : IEntityTypeConfiguration<EnvVariableRecord>
{
    public void Configure(EntityTypeBuilder<EnvVariableRecord> variable)
    {
        variable.ToTable("EnvVariables");
        variable.HasKey(v => v.Name);
    }
}
