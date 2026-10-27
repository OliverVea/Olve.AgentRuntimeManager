using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// A session that was working when the previous server stopped: its latest run
/// (<paramref name="RunId"/>, for the provider session <paramref name="ProviderSessionId"/>), and
/// <paramref name="ResumeRunId"/> for a new run if the provider has to resume it, with the
/// session's environment variables <paramref name="Env"/> (null: none) and its repositories
/// <paramref name="Repos"/> (null: none; checked out already, or checked out by a run launched again).
/// <paramref name="Resume"/> and <paramref name="Messages"/>: how the run was launched (the provider
/// session it resumed, if it did, and the messages it was given), to launch it again the same way
/// if its agent never started. <paramref name="Tools"/>: ARM's tools its agent gets (M10; empty: none at
/// all; null: the provider's built-ins), on a run launched again or resumed as at its start.
/// </summary>
public sealed record AgentRecovery(Guid SessionId, string Prompt, string Model, string ProviderSessionId, Guid RunId, Guid ResumeRunId,
    IReadOnlyDictionary<string, string>? Env = null, IReadOnlyList<RepoRecord>? Repos = null,
    string? Resume = null, IReadOnlyList<string>? Messages = null, IReadOnlyList<string>? Tools = null);
