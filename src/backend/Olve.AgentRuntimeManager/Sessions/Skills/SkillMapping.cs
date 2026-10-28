using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>Domain → contract.</summary>
public static class SkillMapping
{
    public static Skill ToDto(this SkillRecord skill) => new()
    {
        Name = skill.Name,
        Description = skill.Description,
        Default = skill.IsDefault,
        Source = new SkillSource { Url = skill.SourceUrl, Ref = skill.SourceRef, Path = skill.SourcePath },
        UpdatedAt = skill.UpdatedAt,
    };
}
