namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// What a provider needs to start a session's agent. <paramref name="Attempt"/> counts from 1;
/// <paramref name="ProviderSessionId"/> is the id to give the agent, new for every attempt (a
/// failed attempt's id stays taken). <paramref name="RunId"/> names this launch, so a restarted
/// server can tell its traces from an earlier attempt's (docs/GENTLE-RESTART.md).
/// <paramref name="Env"/>: the session's environment variables for the agent (null: none).
/// <paramref name="Messages"/>: what it's told after the prompt, in order (M11; null: nothing).
/// <paramref name="Resume"/>: set when the session continues (M11): the provider session to resume,
/// with <paramref name="Messages"/> as what it's told next instead of the prompt
/// (<paramref name="ProviderSessionId"/> is then unused).
/// </summary>
public sealed record AgentLaunch(Guid SessionId, string Prompt, string Model, int Attempt, Guid ProviderSessionId, Guid RunId,
    IReadOnlyDictionary<string, string>? Env = null, IReadOnlyList<string>? Messages = null, string? Resume = null)
{
    /// <summary>What the agent is told first, in order: the prompt and its messages, or (resuming) just the messages.</summary>
    public IReadOnlyList<string> Input => Resume is null ? [Prompt, .. Messages ?? []] : [.. Messages ?? []];
}
