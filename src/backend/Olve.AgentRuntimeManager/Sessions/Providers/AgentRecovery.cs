namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// A session that was working when the previous server stopped: its latest run
/// (<paramref name="RunId"/>, for the provider session <paramref name="ProviderSessionId"/>), and
/// <paramref name="ResumeRunId"/> for a new run if the provider has to resume it, with the
/// session's environment variables <paramref name="Env"/> (null: none).
/// </summary>
public sealed record AgentRecovery(Guid SessionId, string Prompt, string Model, string ProviderSessionId, Guid RunId, Guid ResumeRunId,
    IReadOnlyDictionary<string, string>? Env = null);
