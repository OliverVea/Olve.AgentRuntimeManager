using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Tools;

/// <summary>
/// What ARM started the server with (<see cref="ToolsLaunch"/>): the workplace, the journal, the
/// tools to serve and the session's skills (M10b), in the order given.
/// </summary>
internal sealed record ToolsOptions(string Workplace, string Journal, IReadOnlyList<string> Tools, IReadOnlyList<ToolsSkill> Skills)
{
    /// <summary>The options from the command line; null with what's wrong in <paramref name="error"/>.</summary>
    public static ToolsOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? workplace = null, journal = null;
        var tools = new List<string>();
        var skills = new List<ToolsSkill>();
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case ToolsLaunch.Workplace when value is not null:
                    workplace = value;
                    i++;
                    break;
                case ToolsLaunch.Journal when value is not null:
                    journal = value;
                    i++;
                    break;
                case ToolsLaunch.Tool when value is not null:
                    if (ToolCatalog.Find(value) is null)
                    {
                        error = $"unknown tool '{value}' (known: {string.Join(", ", ToolCatalog.All.Select(tool => tool.Name))}).";
                        return null;
                    }

                    if (!tools.Contains(value))
                    {
                        tools.Add(value);
                    }

                    i++;
                    break;
                case ToolsLaunch.Skill when value is not null:
                    var separator = value.IndexOf('=', StringComparison.Ordinal);
                    if (separator <= 0 || separator == value.Length - 1)
                    {
                        error = $"{ToolsLaunch.Skill} takes <name>=<folder>, not '{value}'.";
                        return null;
                    }

                    var skill = new ToolsSkill(value[..separator], Path.GetFullPath(value[(separator + 1)..]));
                    if (skills.Any(other => other.Name == skill.Name))
                    {
                        error = $"the skill '{skill.Name}' is given twice.";
                        return null;
                    }

                    skills.Add(skill);
                    i++;
                    break;
                default:
                    error = $"unexpected argument '{args[i]}'.";
                    return null;
            }
        }

        if (workplace is null || journal is null)
        {
            error = $"{ToolsLaunch.Workplace} <folder> and {ToolsLaunch.Journal} <file> are required.";
            return null;
        }

        error = null;
        return new ToolsOptions(Path.GetFullPath(workplace), Path.GetFullPath(journal), tools, skills);
    }
}
