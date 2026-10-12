using System.Globalization;
using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Sessions.Activities;

/// <summary>
/// Text made into one line of a session's activity: no line breaks or control characters, at most
/// <see cref="MaxLength"/> characters (UTF-16 code units, as JavaScript counts them), cut between
/// two characters (never inside one, or inside an emoji) and marked with <c>…</c>.
/// </summary>
public static partial class ActivityText
{
    public const int MaxLength = 200;

    /// <summary>
    /// The agent's text: its first line with something in it, without the cheap-to-drop Markdown
    /// (code fences, heading, quote and list markers, <c>**bold**</c>, backticks; a link as
    /// <c>text (url)</c>). Null when there's nothing in it.
    /// </summary>
    public static string? FirstLine(string? text)
    {
        foreach (var line in Lines(text))
        {
            if (Fence().IsMatch(line))
            {
                continue;
            }

            var plain = Flatten(Link().Replace(Bold().Replace(LineMarker().Replace(line, ""), "$1"), Linked).Replace("`", ""));
            if (plain.Length > 0)
            {
                return Bound(plain);
            }
        }

        return null;
    }

    /// <summary>
    /// What went wrong, in one line: the first line that says so (error, fatal, denied, failed,
    /// exception), else the last one with something in it, as the web UI previews a failed tool
    /// result. Null when there's nothing in it.
    /// </summary>
    public static string? ErrorLine(string? text)
    {
        var lines = Lines(text).Select(Flatten).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? null : Bound(lines.FirstOrDefault(l => SaysWhatFailed().IsMatch(l)) ?? lines[^1]);
    }

    /// <summary>Any text as one line: whitespace (line breaks included) as single spaces, terminal escapes and control characters dropped.</summary>
    public static string Flatten(string text) =>
        Whitespace().Replace(Control().Replace(Escape().Replace(text, ""), ""), " ").Trim();

    /// <summary>At most <see cref="MaxLength"/> characters, cut between two of them and marked with <c>…</c>.</summary>
    public static string Bound(string line)
    {
        if (line.Length <= MaxLength)
        {
            return line;
        }

        var cut = 0;
        while (cut < line.Length)
        {
            var next = StringInfo.GetNextTextElementLength(line.AsSpan(cut));
            if (cut + next > MaxLength - 1)
            {
                break;
            }

            cut += next;
        }

        return $"{line[..cut].TrimEnd()}…";
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string Linked(Match link) =>
        link.Groups["text"].Value == link.Groups["url"].Value
            ? link.Groups["url"].Value
            : $"{link.Groups["text"].Value} ({link.Groups["url"].Value})";

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex Fence();

    /// <summary>Heading, quote and list markers (also nested: <c>&gt; - </c>).</summary>
    [GeneratedRegex(@"^(?:(?:#{1,6}|>|[-*+]|\d{1,9}[.)])(?:\s+|$))+")]
    private static partial Regex LineMarker();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"error|fatal|denied|failed|exception", RegexOptions.IgnoreCase)]
    private static partial Regex SaysWhatFailed();

    /// <summary>ANSI terminal sequences (colours, cursor moves), as tools print them.</summary>
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Escape();

    [GeneratedRegex(@"[\p{Cc}-[\s]]")]
    private static partial Regex Control();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
