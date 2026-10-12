using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Sessions.Activities;

/// <summary>
/// What a tool call is about, in short: its <c>description</c>, else its <c>command</c> (without
/// set-up: <c>cd</c>, <c>export</c>, <c>eval</c>), <c>file_path</c>, <c>pattern</c> or <c>url</c>,
/// else its input as JSON. The same rules as the web UI's session page (<c>gist</c> in
/// <c>src/frontend/src/sessions/conversation.ts</c>); both are tested against
/// <c>tool-gist.cases.json</c> next to it, so they can't drift apart.
/// </summary>
public static partial class ToolGist
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Of(JsonElement? input)
    {
        string? Text(string key) =>
            input is { ValueKind: JsonValueKind.Object } i && i.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        return Text("description")
            ?? (Text("command") is { } command ? Shell(command) : null)
            ?? Text("file_path")
            ?? Text("pattern")
            ?? Text("url")
            ?? (input is { } json ? JsonSerializer.Serialize(json, Compact) : "{}");
    }

    /// <summary>A shell command's first line without its set-up, which says nothing about what it does.</summary>
    public static string Shell(string command)
    {
        var parts = Separator().Split(command.Split('\n')[0]).Where(p => p.Length > 0 && !SetUp().IsMatch(p)).ToList();
        return parts.Count > 0 ? string.Join(" && ", parts) : command;
    }

    [GeneratedRegex(@"\s*(?:&&|;)\s*")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(cd|export|eval)\b")]
    private static partial Regex SetUp();
}
