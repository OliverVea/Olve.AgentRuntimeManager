using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Copies a session's skills into its folder before the agent starts (M10b), as the agent's user:
/// the registry's folder, files and subfolders (links as links), to <see cref="SupervisorSkill.Target"/>.
/// One that's there already is left as it is: a session keeps the copy it started with. The copy is
/// made next to its target and renamed into place, so a target is never half a skill.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class SkillCopy
{
    /// <summary>Copies <paramref name="skill"/>; the error if that failed.</summary>
    public static string? Copy(SupervisorSkill skill, SupervisorLog log)
    {
        if (Posix.ExistsNoFollow(skill.Target))
        {
            log.Write($"skill '{skill.Name}': {skill.Target} is there already; left as it is");
            return null;
        }

        var pending = Path.Combine(Path.GetDirectoryName(skill.Target)!, $".{Path.GetFileName(skill.Target)}.arm-copy");
        try
        {
            if (!Directory.Exists(skill.Source))
            {
                throw new DirectoryNotFoundException($"{skill.Source} isn't a folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(skill.Target)!);
            if (Posix.ExistsNoFollow(pending))
            {
                // Half a copy from a run that died.
                Directory.Delete(pending, recursive: true);
            }

            CopyEntry(skill.Source, pending);
            Directory.Move(pending, skill.Target);
            log.Write($"skill '{skill.Name}': copied {skill.Source} to {skill.Target}");
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RemovePending(pending);
            var error = $"Copying skill '{skill.Name}' failed: {exception.Message}";
            log.Write(error);
            return error;
        }
    }

    private static void CopyEntry(string source, string destination)
    {
        if (new FileInfo(source).LinkTarget is { } target)
        {
            File.CreateSymbolicLink(destination, target);
            return;
        }

        if (!Directory.Exists(source))
        {
            File.Copy(source, destination);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            CopyEntry(entry, Path.Combine(destination, Path.GetFileName(entry)));
        }

        // Last: a read-only folder's copy is filled before it becomes read-only.
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static void RemovePending(string pending)
    {
        try
        {
            if (Posix.ExistsNoFollow(pending))
            {
                Directory.Delete(pending, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the copy's own failure is what the session hears of.
        }
    }
}
