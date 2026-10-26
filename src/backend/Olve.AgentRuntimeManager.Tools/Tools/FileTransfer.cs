using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_copy</c> and <c>arm_file_move</c>: a file or a folder (all it holds) to
/// <c>destination_path</c>, which is the new path itself, not a folder to put it in. An existing
/// target is refused unless <c>overwrite</c>, which replaces it whole (a folder too, not merged).
/// Links are copied as links, never followed.
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

        if (source == destination)
        {
            return ToolResult.Error($"The source and the destination are both {source}.");
        }

        var folder = ToolPaths.IsRealDirectory(source);
        if (folder && ToolPaths.IsWithin(destination, source))
        {
            return ToolResult.Error($"{destination} is inside {source}: a folder can't be {(move ? "moved" : "copied")} into itself.");
        }

        if (ToolPaths.Exists(destination))
        {
            if (!overwrite)
            {
                return ToolResult.Error($"{destination} exists: set `overwrite` to replace it.");
            }

            if (ToolPaths.IsWithin(source, destination))
            {
                return ToolResult.Error($"{source} is inside {destination}: replacing it would delete the source.");
            }

            Delete(destination);
        }

        if (Path.GetDirectoryName(destination) is { } parent)
        {
            Directory.CreateDirectory(parent);
        }

        if (move)
        {
            MoveEntry(source, destination, folder);
        }
        else
        {
            CopyEntry(source, destination);
        }

        var what = folder ? "the folder " : "";
        return ToolResult.Ok($"{(move ? "Moved" : "Copied")} {what}{source} to {destination}.");
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

    private static void MoveEntry(string source, string destination, bool folder)
    {
        if (!folder)
        {
            // rename(2), or a copy and delete across filesystems (.NET does that for files); a link moves as a link.
            File.Move(source, destination);
            return;
        }

        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (!Directory.Exists(destination) && Directory.Exists(source))
        {
            // Another filesystem (EXDEV): a folder can't be renamed there, only copied.
            CopyEntry(source, destination);
            DeleteTree(source);
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
}
