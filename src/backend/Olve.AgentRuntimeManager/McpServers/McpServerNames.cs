using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>
/// Which names an MCP server may have (the contract's <c>McpServerName</c>): lowercase letters,
/// digits and <c>-</c>, starting with a letter, at most <see cref="MaxLength"/> characters. Without
/// <c>_</c> a name can't be one of ARM's tools, and <c>__</c> always splits server from tool.
/// </summary>
public static partial class McpServerNames
{
    public const int MaxLength = 32;

    public static bool IsValid(string name) => name.Length <= MaxLength && ValidName().IsMatch(name);

    /// <summary><c>arm</c> and <c>arm-*</c>: ARM's own.</summary>
    public static bool IsReserved(string name) => name == "arm" || name.StartsWith("arm-", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex ValidName();
}
