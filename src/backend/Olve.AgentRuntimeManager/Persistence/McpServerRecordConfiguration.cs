using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Olve.AgentRuntimeManager.McpServers;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>The <c>McpServers</c> table: one row per registered server (M10c), its arguments and variables as JSON text.</summary>
public sealed class McpServerRecordConfiguration : IEntityTypeConfiguration<McpServerRecord>
{
    public void Configure(EntityTypeBuilder<McpServerRecord> server)
    {
        server.ToTable("McpServers");
        server.HasKey(s => s.Name);
        server.Property<IReadOnlyList<string>?>(nameof(McpServerRecord.Args)).HasConversion(JsonColumn.Converter<IReadOnlyList<string>>(), JsonColumn.Comparer<IReadOnlyList<string>>()).IsRequired();
        server.Property<IReadOnlyDictionary<string, string>?>(nameof(McpServerRecord.Env)).HasConversion(JsonColumn.Converter<IReadOnlyDictionary<string, string>>(), JsonColumn.Comparer<IReadOnlyDictionary<string, string>>()).IsRequired();
    }
}
