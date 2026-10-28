using Olve.AgentRuntimeManager.Sessions.Skills;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Skills;

/// <summary>An <see cref="ISkillStore"/> in a dictionary.</summary>
public sealed class InMemorySkillStore : ISkillStore
{
    private readonly Dictionary<string, SkillRecord> _skills = new(StringComparer.Ordinal);

    public IReadOnlyList<SkillRecord> List() => [.. _skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];

    public SkillRecord? Find(string name) => _skills.GetValueOrDefault(name);

    public void Set(SkillRecord skill) => _skills[skill.Name] = skill;

    public bool Delete(string name) => _skills.Remove(name);

    public InMemorySkillStore With(string name, bool isDefault, string description = "A skill.")
    {
        Set(new SkillRecord
        {
            Name = name, Description = description, IsDefault = isDefault, SourceUrl = $"https://example.invalid/{name}.git", UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        return this;
    }
}
