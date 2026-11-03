using System.Diagnostics;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// <c>remove-folder</c> (#22): empties a deleted session's retired folder (<see cref="RetiredFolders"/>)
/// as the agent user, so a link in it can only lead to what that user may delete anyway. Links are
/// removed, never followed. The folder itself stays: it's in the sessions folder, whose entries only
/// ARM may remove. What's gone already (another pass at the same folder) is fine. Bounded by
/// <see cref="Deadline"/>, since ARM can't stop it once it runs as the agent user: what's left
/// then is for a later pass.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class FolderEmptier
{
    /// <summary>Below ARM's wait for a removal (25 s), so this says it stopped before ARM gives up on it.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>Null when nothing is left in the folder (or there's no folder), else the first error.</summary>
    public static string? Empty(string folder, TimeSpan? deadline = null)
    {
        if (!Posix.ExistsNoFollow(folder))
        {
            return null;
        }

        var info = new DirectoryInfo(folder);
        if (info.LinkTarget is not null || !info.Exists)
        {
            return $"{folder} isn't a folder; left as it is.";
        }

        var clock = Stopwatch.StartNew();
        var limit = deadline ?? Deadline;
        string? error = null;
        EmptyInto(info, () => clock.Elapsed > limit, ref error);
        return clock.Elapsed > limit && error is null ? $"Emptying {folder} took more than {limit.TotalSeconds:0} s; the rest is left for later." : error;
    }

    /// <summary>Removes what's in <paramref name="directory"/>, deepest first, until <paramref name="late"/>.</summary>
    private static void EmptyInto(DirectoryInfo directory, Func<bool> late, ref string? error)
    {
        // A folder made read-only (Go's module cache is 0555) can't be emptied as it is: its owner,
        // the agent user, may make it writable. One of another user's stays as it is.
        try
        {
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((directory.UnixFileMode & owner) != owner)
            {
                directory.UnixFileMode |= owner;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not its owner: the deletes below say what's left.
        }

        FileSystemInfo[] entries;
        try
        {
            entries = [.. directory.EnumerateFileSystemInfos()];
        }
        catch (DirectoryNotFoundException)
        {
            return; // gone already
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error ??= $"Emptying {directory.FullName} failed: {exception.Message}";
            return;
        }

        foreach (var entry in entries)
        {
            if (late())
            {
                return;
            }

            try
            {
                // A link (to a folder or not) is removed as itself: its LinkTarget is set.
                if (entry is DirectoryInfo { LinkTarget: null } child)
                {
                    EmptyInto(child, late, ref error);
                    if (late())
                    {
                        return;
                    }

                    child.Delete();
                }
                else
                {
                    entry.Delete();
                }
            }
            catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
            {
                // Gone already.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error ??= $"Emptying {directory.FullName} failed: {exception.Message}";
            }
        }
    }
}
