namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// How <c>skill-install</c> ended: the process's exit code (<see cref="SkillInstaller"/> says which
/// means what), the installed <c>SKILL.md</c>'s text for stdout, and the error for stderr.
/// </summary>
internal readonly record struct SkillInstallResult(int ExitCode, string? Output, string? Error)
{
    public static SkillInstallResult Installed(string skillFile) => new(0, skillFile, null);

    public static SkillInstallResult Failed(int exitCode, string error) => new(exitCode, null, error);
}
