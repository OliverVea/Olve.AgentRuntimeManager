namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// A skill the supervisor copies for the session before it starts the agent (M10b): the registry's
/// folder <see cref="Source"/> (a <c>SKILL.md</c> and whatever the skill brings along) to
/// <see cref="Target"/> in the session's folder. One already at <see cref="Target"/> is left as it
/// is (a continuation or retry keeps the copy the session started with). ARM works out every path.
/// </summary>
public sealed record SupervisorSkill
{
    /// <summary>The registry's name for it, what the session named in <c>skills</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The registry's folder for it, read as is.</summary>
    public required string Source { get; init; }

    /// <summary>The session's copy: <c>&lt;session folder&gt;/skills/&lt;name&gt;</c>.</summary>
    public required string Target { get; init; }
}
