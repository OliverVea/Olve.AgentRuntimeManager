namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A <c>PUT /api/skills/{name}</c> body.</summary>
public sealed record InstallSkillBody(SkillSourceBody Source, bool? Default = null);
