namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A session as the API returns it.</summary>
public sealed record SessionBody(
    Guid Id,
    string Status,
    int? QueuePosition,
    string Prompt,
    string Provider,
    string Model,
    string Caller,
    int? TimeoutSeconds,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? ProviderSessionId,
    int? ExitCode,
    string? Error,
    string? KillReason,
    string? KillSource,
    string? KillCaller);
