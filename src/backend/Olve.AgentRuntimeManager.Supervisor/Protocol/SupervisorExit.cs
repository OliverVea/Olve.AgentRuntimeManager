namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary><c>exit.json</c>: how the run's agent ended, written (atomically) once all its output is on disk.</summary>
public sealed record SupervisorExit
{
    public required Guid RunId { get; init; }

    /// <summary>The agent's exit code (128 + the signal when a signal ended it).</summary>
    public required int ExitCode { get; init; }

    /// <summary>Set when the agent couldn't be started at all.</summary>
    public string? Error { get; init; }

    public required DateTimeOffset EndedAt { get; init; }
}
