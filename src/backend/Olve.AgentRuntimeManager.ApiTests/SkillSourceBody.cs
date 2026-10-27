namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>Where a skill comes from: a folder of a git repository.</summary>
public sealed record SkillSourceBody(string Url, string? Ref = null, string? Path = null);
