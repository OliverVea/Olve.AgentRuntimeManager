using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_read</c>: a text file as numbered lines (<c>cat -n</c>), from line <c>offset</c>
/// (1-based), at most <c>limit</c> of them. Long lines and long reads are cut, saying how to read
/// on, so a result stays bounded (it's journaled too). Folders and binary files are refused.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FileReadTool
{
    public const int DefaultLimit = 2000;
    public const int MaxLineLength = 2000;
    public const int MaxOutput = 60_000;

    /// <summary>How much of the start is checked for a NUL byte, which text has none of.</summary>
    private const int BinaryProbe = 8192;

    public static ToolResult Run(ToolArguments arguments, ToolContext context)
    {
        var filePath = arguments.RequiredString("file_path");
        var offset = arguments.OptionalInteger("offset") ?? 1;
        var limit = arguments.OptionalInteger("limit") ?? DefaultLimit;
        if (offset < 1)
        {
            arguments.Problems.Add("`offset` is a line number, from 1.");
        }

        if (limit < 1)
        {
            arguments.Problems.Add("`limit` must be at least 1.");
        }

        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        var path = ToolPaths.Resolve(context.Workplace, filePath);
        if (Directory.Exists(path))
        {
            return ToolResult.Error($"{path} is a folder, not a file: list it with arm_bash (ls).");
        }

        if (!File.Exists(path))
        {
            return ToolResult.Error($"{path} doesn't exist.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (IsBinary(stream))
        {
            return ToolResult.Error($"{path} is a binary file (it has NUL bytes): not read. Look at it with arm_bash (file, xxd).");
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        long number = 0, last = 0;
        string? line;
        string? stop = null;
        while ((line = reader.ReadLine()) is not null)
        {
            number++;
            if (number < offset)
            {
                continue;
            }

            if (number >= offset + limit)
            {
                stop = $"[More lines follow line {last}: read on with offset {last + 1}.]";
                break;
            }

            var shown = line.Length > MaxLineLength
                ? string.Create(CultureInfo.InvariantCulture, $"{line[..MaxLineLength]} [... line cut: {line.Length} characters]")
                : line;
            if (text.Length + shown.Length > MaxOutput && last >= offset)
            {
                stop = $"[Output cap reached after line {last}: read on with offset {last + 1}, or a smaller limit.]";
                break;
            }

            text.Append(CultureInfo.InvariantCulture, $"{number,6}\t{shown}\n");
            last = number;
        }

        if (number == 0)
        {
            return ToolResult.Ok($"{path} is empty.");
        }

        if (last == 0)
        {
            return ToolResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{path} has {number} lines: offset {offset} is past its end."));
        }

        return ToolResult.Ok(stop is null ? text.ToString().TrimEnd('\n') : text + stop);
    }

    private static bool IsBinary(FileStream stream)
    {
        var buffer = new byte[BinaryProbe];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }
}
