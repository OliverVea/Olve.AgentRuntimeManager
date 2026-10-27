using Microsoft.EntityFrameworkCore;
using Olve.AgentRuntimeManager.Sessions.Skills;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary><see cref="ISkillStore"/> on EF Core: a short-lived context per call.</summary>
public sealed class EfSkillStore(IDbContextFactory<ArmDbContext> contexts) : ISkillStore
{
    public IReadOnlyList<SkillRecord> List()
    {
        using var db = contexts.CreateDbContext();
        return db.Skills.AsNoTracking().OrderBy(s => s.Name).ToList();
    }

    public SkillRecord? Find(string name)
    {
        using var db = contexts.CreateDbContext();
        return db.Skills.AsNoTracking().SingleOrDefault(s => s.Name == name);
    }

    public void Set(SkillRecord skill)
    {
        using var db = contexts.CreateDbContext();
        if (db.Skills.Any(s => s.Name == skill.Name))
        {
            db.Skills.Update(skill);
        }
        else
        {
            db.Skills.Add(skill);
        }

        db.SaveChanges();
    }

    public bool Delete(string name)
    {
        using var db = contexts.CreateDbContext();
        return db.Skills.Where(s => s.Name == name).ExecuteDelete() > 0;
    }
}
