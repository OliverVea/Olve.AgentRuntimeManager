using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>
/// For <c>dotnet ef migrations add</c>: builds the context without booting the app (whose host
/// needs the generated API and its own configuration). Never connects.
/// </summary>
public sealed class ArmDbContextDesignFactory : IDesignTimeDbContextFactory<ArmDbContext>
{
    public ArmDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ArmDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
