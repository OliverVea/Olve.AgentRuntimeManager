using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>The skills API's error codes (stable once published, docs/STANDARDS.md).</summary>
public static class SkillErrors
{
    public static ArmError InvalidName(string name) =>
        ArmError.Create(ArmErrors.InvalidRequest, $"'{name}' is not a valid skill name (lowercase letters, digits and '-', not starting with '-').");

    public static ArmError NotFound(string name) =>
        ArmError.Create("SKILL_NOT_FOUND", $"No skill '{name}' is registered.");

    /// <summary>The skill's repository (or ref) couldn't be fetched: git's own message.</summary>
    public static ArmError SourceFailed(string url, string message) =>
        ArmError.Create("SKILL_SOURCE_FAILED", $"Fetching the skill from {url} failed: {message}");

    /// <summary>The source has no readable <c>SKILL.md</c> at its path: what's wrong with it.</summary>
    public static ArmError Invalid(string message) =>
        ArmError.Create("INVALID_SKILL", message);
}
