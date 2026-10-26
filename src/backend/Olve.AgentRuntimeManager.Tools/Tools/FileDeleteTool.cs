using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_delete</c>: a file, or a folder and all it holds only with <c>recursive</c>. A link
/// is deleted itself, never what it points to, even a link to a folder with <c>recursive</c>.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FileDeleteTool
{
    public static ToolResult Run(ToolArguments arguments, ToolContext context)
    {
        var filePath = arguments.RequiredString("file_path");
        var recursive = arguments.Flag("recursive");
        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        var path = ToolPaths.Resolve(context.Workplace, filePath);
        if (!ToolPaths.Exists(path))
        {
            return ToolResult.Error($"{path} doesn't exist.");
        }

        if (ToolPaths.IsLink(path) || !Directory.Exists(path))
        {
            // unlink(2): a link (to anything) or a file.
            File.Delete(path);
            return ToolResult.Ok($"Deleted {path}.");
        }

        if (!recursive)
        {
            return ToolResult.Error($"{path} is a folder: set `recursive` to delete it and everything in it.");
        }

        FileTransfer.DeleteTree(path);
        return ToolResult.Ok($"Deleted the folder {path} and everything in it.");
    }
}
