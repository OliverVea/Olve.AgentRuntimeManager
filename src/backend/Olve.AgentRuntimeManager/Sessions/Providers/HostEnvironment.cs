namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// What of ARM's own environment reaches what runs as the agent (the agent, its checkouts' git, a
/// skill install's git): the host's <c>PATH</c>, <c>HOME</c>, user and locale, nothing else. With an
/// agent user, the supervisor side swaps in that user's <c>HOME</c> and name (<c>AgentAccount.Identify</c>).
/// </summary>
public static class HostEnvironment
{
    public static readonly string[] PassedThrough = ["PATH", "HOME", "USER", "LANG", "LC_ALL", "TERM", "TMPDIR"];

    /// <summary>
    /// <paramref name="environment"/> with the passed-through variables that are set on top (the
    /// host's win: their names are reserved, so a session's variables never meet them).
    /// </summary>
    public static Dictionary<string, string> With(IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var name in PassedThrough)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                result[name] = value;
            }
        }

        return result;
    }
}
