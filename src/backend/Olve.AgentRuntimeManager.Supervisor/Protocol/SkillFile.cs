namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// A skill's <c>SKILL.md</c> (M10b; the open Agent Skills format): YAML frontmatter between
/// <c>---</c> lines with at least <c>name</c> and <c>description</c>, then the instructions
/// (markdown). Only what ARM needs is read: flat <c>key: value</c> pairs, plain or quoted, and
/// <c>&gt;</c> / <c>|</c> block scalars (a multi-line description) folded to one line; nested
/// structures are skipped. Both ARM (the registry) and <c>olve-arm-tools</c> (the tool's
/// description) read it with this.
/// </summary>
public sealed record SkillFile(IReadOnlyDictionary<string, string> Frontmatter, string Body)
{
    public const string FileName = "SKILL.md";

    public string? Name => Frontmatter.GetValueOrDefault("name");

    public string? Description => Frontmatter.GetValueOrDefault("description");

    /// <summary>
    /// The file parsed, or null with what's wrong in <paramref name="error"/>: no frontmatter, one
    /// that never closes, or no <c>description</c> in it (<c>name</c> may be missing: the registry's
    /// name is what sessions use).
    /// </summary>
    public static SkillFile? Parse(string text, out string? error)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            error = "SKILL.md doesn't start with a YAML frontmatter (a '---' line).";
            return null;
        }

        var end = Array.FindIndex(lines, 1, line => line.TrimEnd() == "---");
        if (end < 0)
        {
            error = "SKILL.md's frontmatter never closes (no second '---' line).";
            return null;
        }

        var frontmatter = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') || line.StartsWith(' ') || line.StartsWith('\t'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+")
            {
                // A block scalar: the indented lines that follow, folded to one line.
                var parts = new List<string>();
                while (i + 1 < end && (lines[i + 1].StartsWith(' ') || lines[i + 1].StartsWith('\t') || string.IsNullOrWhiteSpace(lines[i + 1])))
                {
                    i++;
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        parts.Add(lines[i].Trim());
                    }
                }

                value = string.Join(' ', parts);
            }
            else if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1].Replace("\\\"", "\"");
            }

            frontmatter[key] = value;
        }

        if (string.IsNullOrWhiteSpace(frontmatter.GetValueOrDefault("description")))
        {
            error = "SKILL.md's frontmatter has no 'description'.";
            return null;
        }

        error = null;
        return new SkillFile(frontmatter, string.Join('\n', lines[(end + 1)..]).Trim('\n'));
    }
}
