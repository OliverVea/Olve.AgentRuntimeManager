using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_copy</c> and <c>arm_file_move</c>: a file or a folder (all it holds) to
/// <c>destination_path</c>, which is the new path itself, not a folder to put it in. Links are
/// copied and moved as links, never followed; FIFOs, devices and sockets aren't copied.
/// </summary>
/// <remarks>
/// <c>overwrite</c> replaces a file (or a link) only, and whole: a move is rename(2), atomic; a
/// copy (or a move to another filesystem) is written to a temporary file next to the destination
/// and renamed over it. A folder in the way is refused, overwrite or not: replacing one can't be
/// made safe in one step, so the agent deletes it first (<c>arm_file_delete</c>, recursive).
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class FileTransfer
{
    /// <summary>How much of a name a temporary file keeps, in UTF-8 bytes: with its prefix, well inside NAME_MAX (255).</summary>
    private const int TemporaryNameBytes = 200;

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

        // Where each really is, links in the folders above resolved (not the entry itself: a link is
        // moved as a link), so a path through a link can't copy a folder into itself.
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

        if (ToolPaths.Exists(destination))
        {
            if (ToolPaths.IsRealDirectory(destination))
            {
                return ToolResult.Error($"{destination} is a folder that exists: `overwrite` replaces files only. Delete it first (arm_file_delete with `recursive`) if it should go.");
            }

            if (!overwrite)
            {
                return ToolResult.Error($"{destination} exists: set `overwrite` to replace it.");
            }

            if (folder)
            {
                return ToolResult.Error($"{destination} is a file that exists: `overwrite` replaces a file with a file only. Delete it first (arm_file_delete) if it should go.");
            }

            // A link onto what it points to, or two hard links of one file ("are the same file"):
            // replacing one with the other would destroy the file.
            if (Native.Identity(source) is { } sourceFile && sourceFile == Native.Identity(destination))
            {
                return ToolResult.Error($"{source} and {destination} are the same file.");
            }
        }

        if (Path.GetDirectoryName(destination) is { } parent)
        {
            Directory.CreateDirectory(parent);
        }

        var what = folder ? "the folder " : "";
        if (!move)
        {
            Put(source, destination, folder);
            return ToolResult.Ok($"Copied {what}{source} to {destination}.");
        }

        var error = Native.Rename(source, destination);
        if (error == 0)
        {
            return ToolResult.Ok($"Moved {what}{source} to {destination}.");
        }

        if (error != Native.CrossDevice)
        {
            return ToolResult.Error($"Could not move {source} to {destination} (errno {error}).");
        }

        // Another filesystem: copied whole first; a complete copy is never rolled back.
        Put(source, destination, folder);
        try
        {
            if (folder)
            {
                DeleteTree(source);
            }
            else
            {
                File.Delete(source);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var left = folder && Directory.Exists(source) ? $" ({Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Count()} entries are left there)" : "";
            return ToolResult.Ok($"Copied {what}{source} to {destination} (another filesystem), but the source could not be removed{left}: {exception.Message}");
        }

        return ToolResult.Ok($"Moved {what}{source} to {destination} (copied to another filesystem, then the source deleted).");
    }

    /// <summary><paramref name="path"/> with the links in the folders above it resolved, its last part as it is.</summary>
    private static string Real(string path) =>
        Path.GetDirectoryName(path) is { } parent ? Path.Combine(Posix.RealPath(parent), Path.GetFileName(path)) : path;

    /// <summary>
    /// A copy of <paramref name="source"/> at <paramref name="destination"/>: a file or link through a
    /// temporary file renamed over it (what was there is replaced whole, or not at all); a folder
    /// (where nothing is) copied entry by entry, its partial copy removed if that fails.
    /// </summary>
    private static void Put(string source, string destination, bool folder)
    {
        if (folder)
        {
            try
            {
                CopyEntry(source, destination);
            }
            catch
            {
                RemovePartial(destination);
                throw;
            }

            return;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, TemporaryName(Path.GetFileName(destination)));
        try
        {
            CopyEntry(source, temporary);
            if (Native.Rename(temporary, destination) is not 0 and var error)
            {
                throw new IOException($"Could not put the copy at {destination} (errno {error}).");
            }
        }
        finally
        {
            if (ToolPaths.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary><c>.arm-&lt;8 hex&gt;-</c> and as much of <paramref name="name"/> as fits <see cref="TemporaryNameBytes"/>.</summary>
    private static string TemporaryName(string name)
    {
        var kept = new StringBuilder();
        var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > TemporaryNameBytes)
            {
                break;
            }

            kept.Append(rune.ToString());
        }

        return $".arm-{Guid.NewGuid().ToString("N")[..8]}-{kept}";
    }

    /// <summary>What a failed folder copy made, best effort: its folders made writable first (a read-only one copied as such).</summary>
    private static void RemovePartial(string path)
    {
        try
        {
            if (!ToolPaths.IsRealDirectory(path))
            {
                if (ToolPaths.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Prepend(path))
            {
                File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            DeleteTree(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the copy's own failure is what the agent hears of.
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
