using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_create</c>: a new file with <c>content</c> (UTF-8), its folders made as needed. A
/// file that exists is refused (an edit is <c>arm_file_edit</c>), checked by the create itself
/// (<see cref="FileMode.CreateNew"/>), so it can't overwrite one made meanwhile, nor write through a link.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FileCreateTool
{
    public static ToolResult Run(ToolArguments arguments, ToolContext context)
    {
        var filePath = arguments.RequiredString("file_path");
        var content = arguments.RequiredString("content", allowEmpty: true);
        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        var path = ToolPaths.Resolve(context.Workplace, filePath);
        if (ToolPaths.Exists(path))
        {
            return ToolResult.Error(Directory.Exists(path)
                ? $"{path} is a folder that exists."
                : $"{path} exists: change it with arm_file_edit, or delete it first.");
        }

        if (Path.GetDirectoryName(path) is { } parent)
        {
            if (File.Exists(parent))
            {
                return ToolResult.Error($"{parent} is a file, so {path} can't be made in it.");
            }

            Directory.CreateDirectory(parent);
        }

        var bytes = new UTF8Encoding(false).GetBytes(content);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }
        catch (IOException) when (ToolPaths.Exists(path))
        {
            return ToolResult.Error($"{path} exists: change it with arm_file_edit, or delete it first.");
        }

        var lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
        return ToolResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Created {path} ({lines} lines, {bytes.Length} bytes)."));
    }
}
