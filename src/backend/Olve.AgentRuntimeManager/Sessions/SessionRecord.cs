using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// A session as the runtime keeps it: its resolved configuration (secrets included, which the
/// API never returns — see <see cref="SessionMapping"/>) and its lifecycle.
/// </summary>
public sealed record SessionRecord
{
    public required Guid Id { get; init; }
    public required SessionStatus Status { get; init; }

    /// <summary>1 = next to start; only while queued.</summary>
    public int? QueuePosition { get; init; }

    public required string Prompt { get; init; }
    public required string Provider { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Caller { get; init; }
    public required IReadOnlyDictionary<string, string> Tags { get; init; }
    public required IReadOnlyDictionary<string, string> Env { get; init; }

    /// <summary>Write-only: passed to <c>approved_bash</c> commands only, never returned, logged or evented.</summary>
    public required IReadOnlyDictionary<string, string> SecretEnv { get; init; }

    public required int TimeoutSeconds { get; init; }
    public string? ApprovalPolicy { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required IReadOnlyList<string> Skills { get; init; }
    public required bool Messaging { get; init; }
    public required bool Headless { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? ProviderSessionId { get; init; }
    public int? ExitCode { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public string? KillReason { get; init; }
    public KillSource? KillSource { get; init; }

    /// <summary>The record's secrets are never part of its string form (logs, debugger).</summary>
    public override string ToString() => $"Session {Id} ({Status})";
}
