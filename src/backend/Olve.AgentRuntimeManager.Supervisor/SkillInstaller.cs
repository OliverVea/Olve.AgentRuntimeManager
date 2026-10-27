using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Puts a skill in the registry from git (M10b), as the agent's user (ARM runs <c>skill-install</c>
/// like <c>remove-worktree</c>): the source is fetched shallow into a temporary folder in the
/// registry, its <c>SKILL.md</c> checked, and the folder put in place of <c>&lt;root&gt;/&lt;name&gt;</c>
/// by renames, so what's at that path is always a whole skill (or nothing). The <c>.git</c> folder
/// isn't kept: the registry holds skills, not repositories (the source is ARM's record).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class SkillInstaller
{
    /// <summary>The fetch failed: git's error.</summary>
    public const int FetchFailed = 1;

    /// <summary>The request can't be acted on: a missing field, a name that isn't a folder name, a path that leaves the repository.</summary>
    public const int BadUsage = 2;

    /// <summary>No <c>SKILL.md</c> the parser accepts at the path.</summary>
    public const int InvalidSkill = 3;

    private const int ErrorLength = 2_000;

    /// <summary>Installs the skill; the result says how it went (see the exit codes above).</summary>
    public static async Task<SkillInstallResult> InstallAsync(SupervisorSkillInstall install)
    {
        if (Usage(install) is { } usage)
        {
            return SkillInstallResult.Failed(BadUsage, usage);
        }

        Directory.CreateDirectory(install.Root);
        var temp = Path.Combine(install.Root, $".{install.Name}.arm-install-{Guid.NewGuid():N}");
        try
        {
            if (await FetchAsync(install, temp) is { } fetchError)
            {
                return SkillInstallResult.Failed(FetchFailed, fetchError);
            }

            var folder = install.Path is null ? temp : Path.GetFullPath(Path.Combine(temp, install.Path));
            if (!IsWithin(folder, temp))
            {
                return SkillInstallResult.Failed(BadUsage, $"The path '{install.Path}' leaves the repository.");
            }

            var skillFile = Path.Combine(folder, SkillFile.FileName);
            if (!Directory.Exists(folder) || !File.Exists(skillFile))
            {
                return SkillInstallResult.Failed(InvalidSkill, $"no {SkillFile.FileName} in {Shown(install, folder, temp)}");
            }

            var text = await File.ReadAllTextAsync(skillFile);
            if (SkillFile.Parse(text, out var parseError) is null)
            {
                return SkillInstallResult.Failed(InvalidSkill, parseError!);
            }

            // What git keeps for itself goes; the skill is the folder's files.
            var git = Path.Combine(temp, ".git");
            if (Directory.Exists(git))
            {
                Directory.Delete(git, recursive: true);
            }

            Replace(Path.Combine(install.Root, install.Name), folder);
            return SkillInstallResult.Installed(text);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    /// <summary>Removes <c>&lt;root&gt;/&lt;name&gt;</c>; null when it's gone (or never was), else why not.</summary>
    public static string? Remove(string root, string name)
    {
        var folder = Path.Combine(root, name);
        try
        {
            if (Posix.ExistsNoFollow(folder))
            {
                // Through a rename first: a reader never sees a half-removed skill at its name.
                var removing = Path.Combine(root, $".{name}.arm-remove-{Guid.NewGuid():N}");
                Directory.Move(folder, removing);
                Directory.Delete(removing, recursive: true);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Removing {folder} failed: {exception.Message}";
        }
    }

    private static string? Usage(SupervisorSkillInstall install) =>
        !Path.IsPathFullyQualified(install.Root) ? $"The root must be an absolute path, not '{install.Root}'."
        : !IsFolderName(install.Name) ? $"The name must be a folder name, not '{install.Name}'."
        : string.IsNullOrWhiteSpace(install.Url) ? "The url is required."
        : install.Ref is "" ? "The ref must not be empty."
        : install.Path is "" ? "The path must not be empty."
        : install.GitTimeout <= TimeSpan.Zero ? "The git timeout must be positive."
        : null;

    /// <summary>One plain folder name: nothing that could name another folder or hide in the registry.</summary>
    private static bool IsFolderName(string name) =>
        name.Length > 0 && name is not ("." or "..") && !name.StartsWith('.') && name.IndexOfAny(['/', '\\', '\0']) < 0;

    /// <summary>A shallow fetch of one commit into a fresh repository at <paramref name="temp"/>; the error if git failed.</summary>
    private static async Task<string?> FetchAsync(SupervisorSkillInstall install, string temp)
    {
        var git = new Git(install.Environment, install.GitTimeout);
        var what = install.Ref is null ? install.Url : $"{install.Url} at {install.Ref}";
        return Failed(await git.RunAsync("init", "--quiet", temp), what, "git init", install.GitTimeout)
            ?? Failed(await git.RunAsync("-C", temp, "fetch", "--quiet", "--depth", "1", install.Url, install.Ref ?? "HEAD"), what, "git fetch", install.GitTimeout)
            ?? Failed(await git.RunAsync("-C", temp, "checkout", "--quiet", "FETCH_HEAD"), what, "git checkout", install.GitTimeout);
    }

    /// <summary>
    /// Puts <paramref name="folder"/> at <paramref name="target"/>: what was there is renamed away
    /// first and deleted last, so a failure leaves the old skill (put back) or the new one, never a mix.
    /// </summary>
    private static void Replace(string target, string folder)
    {
        string? old = null;
        if (Posix.ExistsNoFollow(target))
        {
            old = $"{target}.arm-old-{Guid.NewGuid():N}";
            Directory.Move(target, old);
        }

        try
        {
            Directory.Move(folder, target);
        }
        catch (Exception) when (old is not null && !Posix.ExistsNoFollow(target))
        {
            Directory.Move(old, target);
            throw;
        }

        if (old is not null)
        {
            Directory.Delete(old, recursive: true);
        }
    }

    private static string? Failed(GitResult result, string what, string command, TimeSpan timeout)
    {
        if (result.Succeeded)
        {
            return null;
        }

        var error = result.TimedOut
            ? $"Fetching {what} failed: {command} took more than {timeout.TotalSeconds:0} s."
            : $"Fetching {what} failed: {(result.Error.Length > 0 ? result.Error : $"{command} exited with {result.ExitCode}.")}";
        return error.Length > ErrorLength ? error[..ErrorLength] : error;
    }

    /// <summary>The skill's folder as the request named it, not the temporary folder it was fetched into.</summary>
    private static string Shown(SupervisorSkillInstall install, string folder, string temp) =>
        install.Path is null ? $"the root of {install.Url}" : $"{Path.GetRelativePath(temp, folder)} of {install.Url}";

    private static bool IsWithin(string path, string folder) =>
        path == folder || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
