namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A registered skill as the API returns it (M10b).</summary>
public sealed record SkillBody(string Name, string Description, bool Default, SkillSourceBody Source, DateTimeOffset UpdatedAt);
