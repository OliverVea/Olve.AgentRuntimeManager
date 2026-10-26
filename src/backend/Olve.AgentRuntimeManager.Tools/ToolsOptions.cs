using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Tools;

/// <summary>What ARM started the server with (<see cref="ToolsLaunch"/>): the workplace, the journal and the tools to serve.</summary>
internal sealed record ToolsOptions(string Workplace, string Journal, IReadOnlyList<string> Tools)
{
    /// <summary>The options from the command line; null with what's wrong in <paramref name="error"/>.</summary>
    public static ToolsOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? workplace = null, journal = null;
        var tools = new List<string>();
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
        return new ToolsOptions(Path.GetFullPath(workplace), Path.GetFullPath(journal), tools);
    }
}
