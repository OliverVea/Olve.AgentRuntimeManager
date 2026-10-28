namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// What ARM hands <c>olve-arm-supervisor skill-install</c> on its standard input (M10b): the skill
/// to fetch from git and put in the registry as <c>&lt;Root&gt;/&lt;Name&gt;</c>, whole or not at all.
/// Never written to disk: the environment holds secrets.
/// </summary>
public sealed record SupervisorSkillInstall
{
    /// <summary>The registry's folder (absolute), one folder per skill; made if it's missing.</summary>
    public required string Root { get; init; }

    /// <summary>The registry's name for the skill: its folder's name in <see cref="Root"/>, what sessions name in <c>skills</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The repository to fetch: what git takes (https, ssh, a local path or <c>file://</c>).</summary>
    public required string Url { get; init; }

    /// <summary>A branch, tag or commit; null: the remote's <c>HEAD</c>.</summary>
    public string? Ref { get; init; }

    /// <summary>The skill's folder in the repository (its <c>SKILL.md</c> is in it); null: the repository's root.</summary>
    public string? Path { get; init; }

    /// <summary>The environment git runs with (<c>PATH</c>, <c>HOME</c>, <c>GIT_SSH_COMMAND</c>): the same one the checkouts get.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>The longest the fetch (init, fetch and checkout together) may take; then git is killed and the install fails.</summary>
    public TimeSpan GitTimeout { get; init; } = SupervisorLaunch.DefaultGitTimeout;
}
