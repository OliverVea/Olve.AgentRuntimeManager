using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary>
/// Which names an agent's environment variables may have: letters, digits and <c>_</c>, not
/// starting with a digit, and none that ARM sets for its agents itself (the lockdown, the login).
/// </summary>
public static partial class EnvNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LANG", "LC_ALL", "TERM", "TMPDIR", "DISABLE_UPDATES", "ENABLE_CLAUDEAI_MCP_SERVERS",
    };

    private static readonly string[] ReservedPrefixes = ["CLAUDE_", "ARM_"];

    public static bool IsValid(string name) => ValidName().IsMatch(name);

    public static bool IsReserved(string name) =>
        Reserved.Contains(name) || ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidName();
}
