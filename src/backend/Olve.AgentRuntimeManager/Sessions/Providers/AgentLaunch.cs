namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>What a provider needs to start a session's agent.</summary>
public sealed record AgentLaunch(
    Guid SessionId,
    string Prompt,
    string? Model,
    string? Effort,
    string? SystemPrompt,
    IReadOnlyDictionary<string, string> Env);
