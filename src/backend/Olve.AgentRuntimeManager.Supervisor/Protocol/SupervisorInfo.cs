namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// <c>supervisor.json</c>: the running run, written once its agent has started. What a restarted
/// ARM needs to find the run's output and, if the supervisor is gone, its orphaned agent.
/// </summary>
public sealed record SupervisorInfo
{
    public required int Version { get; init; }
    public required Guid RunId { get; init; }

    /// <summary>The supervisor's pid, which is also the process group its agent runs in (it calls <c>setsid</c>).</summary>
    public required int SupervisorPid { get; init; }

    public required int AgentPid { get; init; }

    /// <summary>The agent's start time (<see cref="Posix.StartTime"/>), so a reused pid isn't mistaken for it.</summary>
    public long? AgentStartTime { get; init; }

    /// <summary>Where this run's output starts in <c>output.jsonl</c> (earlier runs of the session come before it).</summary>
    public required long OutputStart { get; init; }

    /// <summary>Where this run's stderr starts in <c>stderr.log</c>.</summary>
    public required long StderrStart { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}
