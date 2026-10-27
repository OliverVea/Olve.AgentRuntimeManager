namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>
/// What ARM hands the supervisor's <c>skill-install</c> on its standard input (M10b), as one JSON
/// document: fetch the skill's repository (<see cref="Url"/> at <see cref="Ref"/>, with
/// <see cref="Environment"/> for git, so <c>GIT_SSH_COMMAND</c> reaches it), require a valid
/// <c>SKILL.md</c> at <see cref="Path"/> (the root when unset), install the folder at
/// <c>&lt;Root&gt;/&lt;Name&gt;</c> (whole or not at all) and print the <c>SKILL.md</c>'s text. Each git
/// command is bounded by <see cref="GitTimeout"/>. Exit 0 installed; 1 the fetch failed (git's message
/// on stderr); 3 no valid <c>SKILL.md</c> there (what's wrong on stderr); 2 usage.
/// </summary>
/// <remarks>
/// Mirrors <c>Olve.AgentRuntimeManager.Supervisor.Protocol.SupervisorSkillInstall</c>, which the
/// supervisor reads (built alongside; to be reconciled into one type when both land). Serialized with
/// <see cref="System.Text.Json.JsonSerializer"/>'s defaults: PascalCase names, the <see cref="TimeSpan"/>
/// in its default string form.
/// </remarks>
public sealed record SupervisorSkillInstall
{
    /// <summary>The registry's root: the skill is installed at <c>&lt;Root&gt;/&lt;Name&gt;</c>.</summary>
    public required string Root { get; init; }

    public required string Name { get; init; }

    /// <summary>The repository's clone URL.</summary>
    public required string Url { get; init; }

    /// <summary>A branch, tag or commit; null: the remote's default branch.</summary>
    public string? Ref { get; init; }

    /// <summary>The skill's folder in the repository; null: the root.</summary>
    public string? Path { get; init; }

    /// <summary>Git's environment: the registered default variables (<c>GIT_SSH_COMMAND</c>, an identity).</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>The longest any one git command may take.</summary>
    public required TimeSpan GitTimeout { get; init; }
}
