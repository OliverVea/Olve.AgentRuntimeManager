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
    /// <summary>Seconds it may run once started; null: no timeout.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The session's own environment variables, as given.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>The registered, non-default environment variables it asked for.</summary>
    public IReadOnlyList<string>? UseEnv { get; init; }

    /// <summary>
    /// The environment its agent gets, resolved when the session was created: the registered
    /// defaults, the ones it asked for, and its own (winning). Every launch, retry and resume uses it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AgentEnv { get; init; }

    /// <summary>How often its agent was started.</summary>
    public int Attempts { get; init; }

    /// <summary>
    /// Attempts the provider refused (<see cref="Providers.AgentOutcome.Unavailable"/>), except
    /// waits for a known usage-limit reset: what <see cref="SessionOptions.ProviderRetries"/> caps.
    /// </summary>
    public int FailedAttempts { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>When its agent last started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>The provider's id for the latest attempt's agent.</summary>
    public string? ProviderSessionId { get; init; }

    /// <summary>
    /// The latest launch of its agent (an attempt, or a resume after a restart): what a restarted
    /// server looks for among the traces its provider left (docs/GENTLE-RESTART.md).
    /// </summary>
    public Guid? RunId { get; init; }
    public int? ExitCode { get; init; }
    /// <summary>Why it failed, or (queued again) why its last attempt did.</summary>
    public string? Error { get; init; }
    public string? KillReason { get; init; }
    public KillSource? KillSource { get; init; }

    /// <summary>The <c>caller</c> of the kill request, when a user killed the session.</summary>
    public string? KillCaller { get; init; }
}
