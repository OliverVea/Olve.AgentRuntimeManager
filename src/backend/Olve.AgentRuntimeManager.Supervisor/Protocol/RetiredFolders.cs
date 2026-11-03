namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// A deleted session's folder, taken out of use (#22): ARM renames <c>&lt;sessions&gt;/&lt;id&gt;</c>
/// to <c>&lt;sessions&gt;/.deleted-&lt;id&gt;</c> (only ARM may change entries in the sessions folder,
/// docs/AGENT-USER.md), the supervisor's <c>remove-folder</c> empties it as the agent user, and ARM
/// removes what's then an empty folder. A name of this shape is known to be garbage, so a removal
/// cut short is finished by the next pass.
/// </summary>
public static class RetiredFolders
{
    public const string Prefix = ".deleted-";

    /// <summary>The name a deleted session's folder is renamed to.</summary>
    public static string Name(Guid sessionId) => $"{Prefix}{sessionId:D}";

    /// <summary>The session a retired folder's name is of; null for any other name.</summary>
    public static Guid? SessionOf(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name[Prefix.Length..], "D", out var id)
        && Name(id) == name
            ? id
            : null;
}
