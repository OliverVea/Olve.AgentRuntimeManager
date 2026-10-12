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
    /// <summary>Where the agent starts: the workplace, or a repository's worktree in it.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// The session's workplace, made (by the supervisor, as the agent's user) if it's new, with the
    /// <see cref="Repos"/> checked out in it. Unset: <see cref="WorkingDirectory"/> (a launch from before repos).
    /// </summary>
    public string? Workplace { get; init; }

    /// <summary>
    /// Repositories to check out in the workplace before the agent starts (M5e); one that's already
    /// there is left as it is. A checkout that fails ends the run without an agent (<c>exit.json</c>
    /// with the error).
    /// </summary>
    public IReadOnlyList<SupervisorRepo> Repos { get; init; } = [];

    /// <summary>The longest any one git command of a checkout may take; then it's killed and the checkout fails.</summary>
    public TimeSpan GitTimeout { get; init; } = DefaultGitTimeout;

    public static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest a checkout of <paramref name="repos"/> repositories may take, given its
    /// <paramref name="gitTimeout"/>: per repository, the wait for another session's checkout
    /// (four git timeouts) and a few slow git commands (the fetch, the default branch, a worktree),
    /// with a minute to spare. The supervisor stops a checkout that runs longer, and ARM waits this
    /// long (and a little) for the hello of a run that checks out.
    /// </summary>
    public static TimeSpan CheckoutLimit(int repos, TimeSpan gitTimeout) =>
        repos == 0 ? TimeSpan.Zero : (gitTimeout * 8 * repos) + TimeSpan.FromMinutes(1);

    /// <summary>
    /// Lines written to the agent's stdin as soon as it starts (its prompt), so a server that goes
    /// away right after the launch can't leave the agent without its input.
    /// </summary>
    public IReadOnlyList<string> Input { get; init; } = [];

    /// <summary>The session's folder: <see cref="SupervisorFiles"/> live here.</summary>
    public required string Folder { get; init; }

    public required string SocketPath { get; init; }

    /// <summary>
    /// The only user allowed on the socket (ARM's; checked with <c>SO_PEERCRED</c>). Agents may run as
    /// another user than ARM (docs/AGENT-USER.md), and the socket is then shared with ARM's group:
    /// this keeps one agent from attaching to another session's supervisor. Unset: anyone who can
    /// open the socket (a launch from before this field).
    /// </summary>
    public int? ClientUid { get; init; }
}
