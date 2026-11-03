using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// The session's large outputs (<c>outputs/</c> in its folder): a result of more
/// than <see cref="MaxText"/> characters is saved whole, by tool-use id, and the agent gets its
/// first and last 5,000 and where the rest is. Images and other binary blocks of an external MCP
/// server's result still reach the agent, and don't count: their head and tail mean nothing.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed partial class ToolOutputs(string folder, long maxFileBytes = ToolOutputs.DefaultMaxFileBytes)
{
    public const int MaxText = CappedOutput.DefaultHead + CappedOutput.DefaultTail;
    public const long DefaultMaxFileBytes = 100_000_000;

    /// <summary>The file for <paramref name="toolUseId"/>'s output, created when something is written to it.</summary>
    public OutputFile Open(string? toolUseId, string extension = ".txt") => new(PathOf(toolUseId, extension), maxFileBytes);

    /// <summary>What the agent is sent of <paramref name="result"/>: itself, or cut with the whole of it saved.</summary>
    public ToolResult Bound(string? toolUseId, ToolResult result)
    {
        if (result.Raw is { } raw)
        {
            return BoundRaw(toolUseId, result, raw);
        }

        return result.Text.Length <= MaxText
            ? result
            : result with { Text = Cut(result.Text, Save(toolUseId, ".txt", result.Text)) };
    }

    /// <summary>
    /// Saves a result the journal can't hold in a line (images, kept as they came) as it was sent,
    /// for the journal to point to; null if it can't be.
    /// </summary>
    public string? SaveSent(string toolUseId, ToolResult result)
    {
        var path = PathOf(toolUseId, ".sent.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, result.Text, new UTF8Encoding(false));
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private ToolResult BoundRaw(string? toolUseId, ToolResult result, JsonObject raw)
    {
        var blocks = (raw["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var texts = blocks.Select(TextOf).OfType<string>().ToList();
        if (raw["structuredContent"]?.ToJsonString(ToolResult.Json) is { } structured)
        {
            texts.Add(structured);
        }

        var readable = string.Join("\n\n", texts);
        if (readable.Length <= MaxText)
        {
            return result;
        }

        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Cut(readable, Save(toolUseId, ".json", result.Text)) });
        foreach (var block in blocks.Where(block => TextOf(block) is null))
        {
            content.Add(block.DeepClone());
        }

        var cut = raw.DeepClone().AsObject();
        cut.Remove("structuredContent");
        cut["content"] = content;
        return ToolResult.Passed(cut);
    }

    /// <summary>A block's text: a text block's, or an embedded text resource's; null for anything else.</summary>
    private static string? TextOf(JsonObject block)
    {
        var text = block["type"] is JsonValue kind && kind.TryGetValue<string>(out var type) && type == "resource" ? block["resource"]?["text"] : block["text"];
        return text is JsonValue value && value.TryGetValue<string>(out var words) ? words : null;
    }

    private static string Cut(string text, string note)
    {
        var output = new CappedOutput();
        output.Append(text);
        return output.ToString(note);
    }

    private string Save(string? toolUseId, string extension, string text)
    {
        using var file = Open(toolUseId, extension);
        file.Write(new UTF8Encoding(false).GetBytes(text));
        file.Close(keep: true);
        return file.Note();
    }

    /// <summary>A tool-use id comes from the client: only a plain one goes into a path.</summary>
    private string PathOf(string? toolUseId, string extension)
    {
        var name = toolUseId is not null && PlainId().IsMatch(toolUseId) ? toolUseId : Guid.NewGuid().ToString("N");
        return Path.Combine(folder, name + extension);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex PlainId();
}
