namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>Settings of the skills registry (M10b; configuration section <c>Skills</c>).</summary>
public sealed class SkillsOptions
{
    public const string Section = "Skills";

    /// <summary>
    /// Where the installed skills' folders are kept (<c>&lt;root&gt;/&lt;name&gt;</c>, each a <c>SKILL.md</c>
    /// and whatever the skill brings along), written by the agents' user (the supervisor installs
    /// them, like it checks out repositories). Empty: <c>olve-arm/skills</c> in the user's local data
    /// folder, next to the sessions' default work root.
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>
    /// The longest an install may take (the fetch of the skill's repository, mostly); then it's
    /// stopped and the install fails. Also bounds each git command of it.
    /// </summary>
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The root as a full path: <see cref="Root"/>, or the default.</summary>
    public string ResolvedRoot() =>
        Path.GetFullPath(Root is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "olve-arm", "skills"));

    /// <summary>The registry's folder for the skill <paramref name="name"/> (a valid name, <see cref="SkillNames"/>).</summary>
    public string FolderOf(string name) => Path.Combine(ResolvedRoot(), name);
}
