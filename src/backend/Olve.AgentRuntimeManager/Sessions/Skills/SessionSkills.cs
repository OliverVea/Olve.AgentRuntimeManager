using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>
/// A new session's skills (M10b), resolved once, when it's created, and stored with it: those it
/// named (<c>[]</c>: none), else the registered skills flagged default, in name order. Every one it
/// names must be registered.
/// </summary>
public static class SessionSkills
{
    /// <summary>The skills, as given, or the problem with the request's names.</summary>
    public static (IReadOnlyList<string>? Skills, ArmError? Problem) Resolve(IReadOnlyList<string>? requested, ISkillStore store)
    {
        var registered = store.List().OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        if (requested is null)
        {
            return ([.. registered.Where(s => s.IsDefault).Select(s => s.Name)], null);
        }

        var known = registered.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(name => !known.Contains(name)).Distinct(StringComparer.Ordinal).ToList();
        return unknown.Count > 0
            ? (null, SessionErrors.UnknownSkill(unknown, [.. known.Order(StringComparer.Ordinal)]))
            : (requested, null);
    }
}
