using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>A session as the runtime keeps it: what it was created with and its lifecycle.</summary>
public sealed record SessionRecord
{
    public required Guid Id { get; init; }
    public required SessionStatus Status { get; init; }

    /// <summary>1 = next to start; only while queued.</summary>
    public int? QueuePosition { get; init; }

    public required string Prompt { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Caller { get; init; }
    public required int TimeoutSeconds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? ProviderSessionId { get; init; }
    public int? ExitCode { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public string? KillReason { get; init; }
    public KillSource? KillSource { get; init; }

    /// <summary>The <c>caller</c> of the kill request, when a user killed the session.</summary>
    public string? KillCaller { get; init; }
}
