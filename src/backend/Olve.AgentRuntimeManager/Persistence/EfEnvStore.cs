using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary><see cref="IEnvStore"/> on EF Core: a short-lived context per call.</summary>
public sealed class EfEnvStore(IDbContextFactory<ArmDbContext> contexts) : IEnvStore
{
    public IReadOnlyList<EnvVariableRecord> List()
    {
        using var db = contexts.CreateDbContext();
        return db.EnvVariables.AsNoTracking().OrderBy(v => v.Name).ToList();
    }

    public void Set(EnvVariableRecord variable)
    {
        using var db = contexts.CreateDbContext();
        if (db.EnvVariables.Any(v => v.Name == variable.Name))
        {
            db.EnvVariables.Update(variable);
        }
        else
        {
            db.EnvVariables.Add(variable);
        }

        db.SaveChanges();
    }

    public bool Delete(string name)
    {
        using var db = contexts.CreateDbContext();
        return db.EnvVariables.Where(v => v.Name == name).ExecuteDelete() > 0;
    }
}
