namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>Where registered skills are kept (their folders are on disk, under <see cref="SkillsOptions.Root"/>).</summary>
public interface ISkillStore
{
    /// <summary>Every registered skill, by name.</summary>
    IReadOnlyList<SkillRecord> List();

    /// <summary>The skill by that name, or null.</summary>
    SkillRecord? Find(string name);

    /// <summary>Registers a skill, or replaces the one with its name.</summary>
    void Set(SkillRecord skill);

    /// <summary>False if there was none by that name.</summary>
    bool Delete(string name);
}
