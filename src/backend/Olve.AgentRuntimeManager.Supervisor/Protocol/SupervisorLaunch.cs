namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// What ARM hands a new supervisor on its standard input: the agent's full command line and
/// environment (the supervisor adds nothing), where the session's files are, and where to listen.
/// Never written to disk: the environment holds secrets.
/// </summary>
public sealed record SupervisorLaunch
{
    public required int Version { get; init; }

    /// <summary>This launch (an attempt or a resume); the files and messages carry it.</summary>
    public required Guid RunId { get; init; }

    public required string Command { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Lines written to the agent's stdin as soon as it starts (its prompt), so a server that goes
    /// away right after the launch can't leave the agent without its input.
    /// </summary>
    public IReadOnlyList<string> Input { get; init; } = [];

    /// <summary>The session's folder: <see cref="SupervisorFiles"/> live here.</summary>
    public required string Folder { get; init; }

    public required string SocketPath { get; init; }
}
