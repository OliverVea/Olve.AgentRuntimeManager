using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_file_edit</c>: replaces <c>old_string</c> with <c>new_string</c>, matched exactly (case,
/// whitespace and line endings as they are). It must be in the file once, unless
/// <c>replace_all</c>, so an edit never lands somewhere the agent didn't mean. UTF-8 only (a file
/// that isn't is refused, not rewritten), a byte order mark kept as it was.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FileEditTool
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ToolResult Run(ToolArguments arguments, ToolContext context)
    {
        var filePath = arguments.RequiredString("file_path");
        var oldString = arguments.RequiredString("old_string");
        var newString = arguments.RequiredString("new_string", allowEmpty: true);
        var replaceAll = arguments.Flag("replace_all");
        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        if (oldString == newString)
        {
            return ToolResult.Error("`old_string` and `new_string` are the same: nothing to change.");
        }

        var path = ToolPaths.Resolve(context.Workplace, filePath);
        if (!ToolPaths.Exists(path))
        {
            return ToolResult.Error($"{path} doesn't exist: make a new file with arm_file_create.");
        }

        if (ToolPaths.NotAFile(path) is { } notAFile)
        {
            return ToolResult.Error(notAFile);
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().Contains((byte)0))
        {
            return ToolResult.Error($"{path} is a binary file (it has NUL bytes): not edited.");
        }

        var preamble = Encoding.UTF8.Preamble;
        var bom = bytes.AsSpan().StartsWith(preamble);
        string text;
        try
        {
            text = Strict.GetString(bom ? bytes[preamble.Length..] : bytes);
        }
        catch (DecoderFallbackException)
        {
            // Decoded leniently and written back, every byte that isn't UTF-8 would be replaced, far from the edit too.
            return ToolResult.Error($"{path} isn't UTF-8 text: not edited. Change it with arm_bash.");
        }

        var count = Count(text, oldString);
        if (count == 0)
        {
            return ToolResult.Error($"`old_string` isn't in {path}: it must match exactly, whitespace and line endings too. Read the file again for its current text.");
        }

        if (count > 1 && !replaceAll)
        {
            return ToolResult.Error(string.Create(CultureInfo.InvariantCulture,
                $"`old_string` is in {path} {count} times: give more of the text around it to make it unique, or set `replace_all` to change every one."));
        }

        var edited = replaceAll ? text.Replace(oldString, newString, StringComparison.Ordinal) : ReplaceFirst(text, oldString, newString);
        var encoded = Encoding.UTF8.GetBytes(edited);
        // In place (truncate and write, as editors do): the file keeps its hard links, owner, mode,
        // ACLs and xattrs, and a read-only folder doesn't stop it.
        File.WriteAllBytes(path, bom ? [.. preamble, .. encoded] : encoded);
        return ToolResult.Ok(count == 1
            ? $"Edited {path}: replaced 1 occurrence."
            : string.Create(CultureInfo.InvariantCulture, $"Edited {path}: replaced all {count} occurrences."));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldString, string newString)
    {
        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), newString, text.AsSpan(index + oldString.Length));
    }
}
