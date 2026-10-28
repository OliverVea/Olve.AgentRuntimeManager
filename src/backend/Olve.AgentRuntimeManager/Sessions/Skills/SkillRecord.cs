namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>A skill registered for agents (<c>/api/skills</c>, M10b): its folder is <c>&lt;Skills:Root&gt;/&lt;Name&gt;</c>.</summary>
public sealed record SkillRecord
{
    /// <summary>What sessions name in <c>skills</c> (the registry's name, not necessarily the frontmatter's).</summary>
    public required string Name { get; init; }

    /// <summary>The <c>SKILL.md</c> frontmatter's <c>description</c>, read when it was installed.</summary>
    public required string Description { get; init; }

    /// <summary>Whether every session that leaves <c>skills</c> unset gets it.</summary>
    public required bool IsDefault { get; init; }

    /// <summary>The repository it was installed from (its clone URL).</summary>
    public required string SourceUrl { get; init; }

    /// <summary>The branch, tag or commit it was installed from; null: the repository's default branch.</summary>
    public string? SourceRef { get; init; }

    /// <summary>Its folder in the repository; null: the root.</summary>
    public string? SourcePath { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
