using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Sessions.Activities;

/// <summary>
/// Text made into one line of a session's activity: no line breaks, control characters or
/// invisible format characters (bidi overrides and isolates, zero-width characters, tags: they
/// could make a line read as something else), at most <see cref="MaxLength"/> characters (UTF-16
/// code units, as JavaScript counts them), cut between two characters (never inside one, or inside
/// an emoji) and marked with <c>…</c>. Only the start of a text is shaped (its first
/// <see cref="MaxShaped"/> characters; of an error, its start and its end), and every pattern has a
/// time limit, so a huge or hostile text costs no more than a short one.
/// </summary>
public static partial class ActivityText
{
    public const int MaxLength = 200;

    /// <summary>How much of a text is looked at: 4 KiB characters (an error: 4 KiB from each end).</summary>
    public const int MaxShaped = 4096;

    /// <summary>
    /// A guard against runaway backtracking on at most <see cref="MaxShaped"/> characters, not a
    /// budget: a loaded host (the gate shares one with other pipelines) takes far longer than usual.
    /// </summary>
    internal const int RegexTimeoutMs = 1000;

    /// <summary>
    /// The agent's text: its first line with something in it, without what's clearly Markdown (code
    /// fences; a heading or list marker followed by a space; balanced <c>**bold**</c> and
    /// <c>`code`</c>; a link <c>[text](https://…)</c> as <c>text (url)</c>). Null when there's
    /// nothing in it.
    /// </summary>
    public static string? FirstLine(string? text)
    {
        foreach (var line in Lines(Head(text ?? "", MaxShaped)))
        {
            if (Fence().IsMatch(line))
            {
                continue;
            }

            var plain = Flatten(Plain(line));
            if (plain.Length > 0)
            {
                return Bound(plain);
            }
        }

        return null;
    }

    /// <summary>
    /// What went wrong, in one line: the first line that says so (error, fatal, denied, failed,
    /// exception; not a count of none, such as <c>0 errors</c>), else the last one with something
    /// in it, as the web UI previews a failed tool result. Null when there's nothing in it.
    /// </summary>
    public static string? ErrorLine(string? text)
    {
        text ??= "";
        if (text.Length > 2 * MaxShaped)
        {
            text = $"{Head(text, MaxShaped)}\n{Tail(text, MaxShaped)}";
        }

        var lines = Lines(text).Select(Flatten).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? null : Bound(lines.FirstOrDefault(SaysWhatFailed) ?? lines[^1]);
    }

    /// <summary>
    /// Any text as one line: whitespace (line breaks included) as single spaces; terminal escapes,
    /// control characters and format characters dropped, but for a zero-width joiner inside an
    /// emoji (👩‍👩‍👧).
    /// </summary>
    public static string Flatten(string text)
    {
        var line = new StringBuilder(Math.Min(text.Length, 2 * MaxShaped));
        var space = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\u001B')
            {
                // A CSI sequence (colours, cursor moves) up to its final character; a lone escape is just dropped.
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    for (i += 2; i < text.Length && text[i] is < '@' or > '~'; i++)
                    {
                    }
                }

                continue;
            }

            var width = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var category = width == 2 ? CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[i + 1])) : CharUnicodeInfo.GetUnicodeCategory(c);
            var kept = category switch
            {
                _ when width == 1 && char.IsWhiteSpace(c) => false,
                UnicodeCategory.Control or UnicodeCategory.Surrogate => false,
                UnicodeCategory.Format => c == '‍' && JoinsEmoji(text, i),
                _ => true,
            };
            if (width == 1 && char.IsWhiteSpace(c))
            {
                space = line.Length > 0;
            }
            else if (kept)
            {
                if (space)
                {
                    line.Append(' ');
                    space = false;
                }

                line.Append(text, i, width);
            }

            i += width - 1;
        }

        return line.ToString();
    }

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

    /// <summary>The first <paramref name="length"/> characters at most, not cutting a surrogate pair.</summary>
    public static string Head(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        return text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
    }

    private static string Tail(string text, int length)
    {
        var start = text.Length - length;
        return text[(char.IsLowSurrogate(text[start]) ? start + 1 : start)..];
    }

    /// <summary>Between two parts of an emoji: after a pictograph (or its variation selector) and before one.</summary>
    private static bool JoinsEmoji(string text, int at)
    {
        static bool Pictograph(int codePoint) =>
            codePoint >= 0x1F000 || CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.OtherSymbol;

        var before = at >= 1 && text[at - 1] == '️'
            || at >= 2 && char.IsSurrogatePair(text[at - 2], text[at - 1]) && Pictograph(char.ConvertToUtf32(text[at - 2], text[at - 1]))
            || at >= 1 && !char.IsSurrogate(text[at - 1]) && Pictograph(text[at - 1]);
        var after = at + 2 < text.Length && char.IsSurrogatePair(text[at + 1], text[at + 2]) && Pictograph(char.ConvertToUtf32(text[at + 1], text[at + 2]))
            || at + 1 < text.Length && !char.IsSurrogate(text[at + 1]) && Pictograph(text[at + 1]);
        return before && after;
    }

    /// <summary>A line without its Markdown; as it is if a pattern takes too long.</summary>
    private static string Plain(string line)
    {
        try
        {
            return Link().Replace(Code().Replace(Bold().Replace(LineMarker().Replace(line, ""), "$1"), "$1"), Linked);
        }
        catch (RegexMatchTimeoutException)
        {
            return line;
        }
    }

    private static bool SaysWhatFailed(string line)
    {
        try
        {
            return Failure().IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string Linked(Match link) =>
        link.Groups["text"].Value == link.Groups["url"].Value
            ? link.Groups["url"].Value
            : $"{link.Groups["text"].Value} ({link.Groups["url"].Value})";

    [GeneratedRegex(@"^(`{3,}|~{3,})", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Fence();

    /// <summary>A heading or list marker at the line's start, followed by a space (or nothing else).</summary>
    [GeneratedRegex(@"^(?:#{1,6}|[-*+]|\d{1,2}[.)])(?:\s+|$)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex LineMarker();

    /// <summary>Balanced bold: <c>**</c> against a word on both sides, not inside one (<c>**kwargs and **args</c> stays).</summary>
    [GeneratedRegex(@"(?<![\w*])\*\*(?=[^\s*])(.+?)(?<=[^\s*])\*\*(?![\w*])", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Bold();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Code();

    /// <summary>A real link to a web page; <c>handlers[name](args)</c> isn't one.</summary>
    [GeneratedRegex(@"\[(?<text>[^\[\]]{1,500})\]\((?<url>https?://[^()\s]{1,2000})\)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<!\b(?:0|no)\s+)(?:error|fatal|denied|failed|exception)", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex Failure();
}
