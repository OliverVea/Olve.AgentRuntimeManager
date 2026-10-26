namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// Where a path a call gives points: a relative one from the workplace, an absolute one as it is.
/// Not confined to the workplace: that would protect nothing while <c>arm_bash</c> exists
/// (docs/MILESTONES.md M10; policies are M8's).
/// </summary>
internal static class ToolPaths
{
    public static string Resolve(string workplace, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workplace, path));

    /// <summary>Whether anything is at <paramref name="path"/>, a link (even a dangling one) included.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

    /// <summary>A symbolic link, whatever it points to (or whether it points anywhere).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Why <paramref name="path"/> (links followed) can't be read as a file; null if it's a regular
    /// file. A FIFO, device or socket is refused: reading one can block for ever, or take what's
    /// meant for another (<c>/dev/stdin</c> is the server's MCP input).
    /// </summary>
    public static string? NotAFile(string path) => Native.Kind(path) switch
    {
        null => ToolPaths.IsLink(path) ? $"{path} is a link to nothing." : $"{path} doesn't exist.",
        FileKind.Regular => null,
        FileKind.Directory => $"{path} is a folder, not a file: list it with arm_bash (ls).",
        var kind => $"{path} isn't a regular file ({kind switch { FileKind.Fifo => "a FIFO", FileKind.Socket => "a socket", FileKind.Device => "a device", _ => "a special file" }}): not opened.",
    };

    /// <summary>A folder itself, not a link to one.</summary>
    public static bool IsRealDirectory(string path) => Directory.Exists(path) && !IsLink(path);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    public static bool IsWithin(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        return path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
