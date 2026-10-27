using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>
/// Which names a skill may have (the contract's <c>SkillName</c>): lowercase letters, digits and
/// <c>-</c>, not starting with <c>-</c>. Also what keeps a name a plain folder name in the registry's root.
/// </summary>
public static partial class SkillNames
{
    public static bool IsValid(string name) => ValidName().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidName();
}
