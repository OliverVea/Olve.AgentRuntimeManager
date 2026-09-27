using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary>
/// A new session's environment: the registered defaults, the registered variables it names in
/// <c>useEnv</c>, and its own <c>env</c> on top. Taken once, when it's created.
/// </summary>
public static class SessionEnv
{
    /// <summary>The environment, or the first problem with the request's names.</summary>
    public static (IReadOnlyDictionary<string, string>? Env, ArmError? Problem) Resolve(CreateSession request, IEnvStore store)
    {
        foreach (var name in request.Env?.Keys ?? [])
        {
            if (EnvErrors.Check(name) is { } problem)
            {
                return (null, problem);
            }
        }

        var registered = store.List().ToDictionary(v => v.Name, StringComparer.Ordinal);
        var unknown = (request.UseEnv ?? []).Where(name => !registered.ContainsKey(name)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            return (null, EnvErrors.UnknownUseEnv(unknown));
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(request.UseEnv ?? [], StringComparer.Ordinal);
        foreach (var variable in registered.Values.Where(v => v.IsDefault || used.Contains(v.Name)))
        {
            env[variable.Name] = variable.Value;
        }

        foreach (var (name, value) in request.Env ?? new Dictionary<string, string>())
        {
            env[name] = value;
        }

        return (env, null);
    }
}
