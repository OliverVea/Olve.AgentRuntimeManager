using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_copy</c> and <c>arm_file_move</c>: a file or a folder (all it holds) to
/// <c>destination_path</c>, which is the new path itself, not a folder to put it in. An existing
/// target is refused unless <c>overwrite</c>, which replaces it whole (a folder too, not merged),
/// and only once the new one is in place: the old one is moved aside first and put back if the
/// copy or move fails. Links are copied and moved as links, never followed; FIFOs, devices and
/// sockets aren't copied.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FileTransfer
{
    public static ToolResult Copy(ToolArguments arguments, ToolContext context) => Run(arguments, context, move: false);

    public static ToolResult Move(ToolArguments arguments, ToolContext context) => Run(arguments, context, move: true);

    /// <summary>A folder and everything in it; links inside are removed themselves, not followed.</summary>
    public static void DeleteTree(string path) => Directory.Delete(path, recursive: true);

    private static ToolResult Run(ToolArguments arguments, ToolContext context, bool move)
    {
        var filePath = arguments.RequiredString("file_path");
        var destinationPath = arguments.RequiredString("destination_path");
        var overwrite = arguments.Flag("overwrite");
        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        var source = Path.TrimEndingDirectorySeparator(ToolPaths.Resolve(context.Workplace, filePath));
        var destination = Path.TrimEndingDirectorySeparator(ToolPaths.Resolve(context.Workplace, destinationPath));
        if (!ToolPaths.Exists(source))
        {
            return ToolResult.Error($"{source} doesn't exist.");
        }

        // Where each really is, links in the folders above resolved (not the entry itself: a link is moved as a link),
        // so a path through a link can't copy a folder into itself or replace what holds the source.
        var realSource = Real(source);
        var realDestination = Real(destination);
        if (realSource == realDestination)
        {
            return ToolResult.Error($"The source and the destination are both {realSource}.");
        }

        var folder = ToolPaths.IsRealDirectory(source);
        if (folder && ToolPaths.IsWithin(realDestination, realSource))
        {
            return ToolResult.Error($"{destination} is inside {source}: a folder can't be {(move ? "moved" : "copied")} into itself.");
        }

        if (!move && !folder && !ToolPaths.IsLink(source) && ToolPaths.NotAFile(source) is { } notAFile)
        {
            return ToolResult.Error(notAFile);
        }

        var replacing = ToolPaths.Exists(destination);
        if (replacing)
        {
            if (!overwrite)
            {
                return ToolResult.Error($"{destination} exists: set `overwrite` to replace it.");
            }

            if (ToolPaths.IsWithin(realSource, realDestination))
            {
                return ToolResult.Error($"{source} is inside {destination}: replacing it would delete the source.");
            }
        }

        if (Path.GetDirectoryName(destination) is { } parent)
        {
            Directory.CreateDirectory(parent);
        }

        if (replacing)
        {
            Replace(destination, () => Transfer(source, destination, move));
        }
        else
        {
            Transfer(source, destination, move);
        }

        var what = folder ? "the folder " : "";
        return ToolResult.Ok($"{(move ? "Moved" : "Copied")} {what}{source} to {destination}.");
    }

    /// <summary><paramref name="path"/> with the links in the folders above it resolved, its last part as it is.</summary>
    private static string Real(string path) =>
        Path.GetDirectoryName(path) is { } parent ? Path.Combine(Posix.RealPath(parent), Path.GetFileName(path)) : path;

    /// <summary>
    /// Puts something new at <paramref name="destination"/> (<paramref name="put"/>), replacing what's
    /// there only once it worked: the old one is renamed aside first, deleted after, and renamed back
    /// if <paramref name="put"/> fails.
    /// </summary>
    private static void Replace(string destination, Action put)
    {
        var aside = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.arm-replaced-{Guid.NewGuid():N}");
        Rename(destination, aside);
        try
        {
            put();
        }
        catch
        {
            if (ToolPaths.Exists(destination))
            {
                Delete(destination);
            }

            Rename(aside, destination);
            throw;
        }

        Delete(aside);
    }

    /// <summary>
    /// Moves (rename(2): the entry itself, a link as a link; a copy and delete to another
    /// filesystem) or copies <paramref name="source"/> to <paramref name="destination"/>, where
    /// nothing is. A copy that fails partway leaves nothing behind.
    /// </summary>
    private static void Transfer(string source, string destination, bool move)
    {
        if (move)
        {
            var error = Native.Rename(source, destination);
            if (error == 0)
            {
                return;
            }

            if (error != Native.CrossDevice)
            {
                throw new IOException($"Could not move {source} to {destination} (errno {error}).");
            }
        }

        try
        {
            CopyEntry(source, destination);
        }
        catch
        {
            if (ToolPaths.Exists(destination))
            {
                Delete(destination);
            }

            throw;
        }

        if (move)
        {
            Delete(source);
        }
    }

    private static void Rename(string from, string to)
    {
        if (Native.Rename(from, to) is not 0 and var error)
        {
            throw new IOException($"Could not rename {from} to {to} (errno {error}).");
        }
    }

    private static void Delete(string path)
    {
        if (ToolPaths.IsRealDirectory(path))
        {
            DeleteTree(path);
        }
        else
        {
            File.Delete(path);
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
            if (ToolPaths.NotAFile(source) is { } notAFile)
            {
                throw new IOException(notAFile);
            }

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
}
