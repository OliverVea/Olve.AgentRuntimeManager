namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A session as the API returns it.</summary>
public sealed record SessionBody(
    Guid Id,
    string Status,
    int? QueuePosition,
    string Prompt,
    string Provider,
    string? Caller,
    Dictionary<string, string> Tags,
    Dictionary<string, string> Env,
    int TimeoutSeconds,
    List<string> Tools,
    List<string> Skills,
    bool Messaging,
    bool Headless,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? ProviderSessionId,
    int? ExitCode,
    string? Summary,
    string? Error,
    string? KillReason,
    string? KillSource);
