namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// Where supervisors listen: a socket per run, <c>&lt;session id&gt;.&lt;first 8 of the run id&gt;.sock</c>
/// in the socket root. Per run, not per session: a session killed during its checkout and continued
/// at once has two supervisors for a moment, and each must keep its own socket (an old run's ARM
/// reaching only its own supervisor, a new run's socket never removed by the old one). Supervisors
/// from before M5e's review listen on <see cref="Legacy"/>.
/// </summary>
public static class SupervisorSockets
{
    public static string Run(string root, Guid sessionId, Guid runId) => Path.Combine(root, $"{sessionId}.{runId.ToString("N")[..8]}.sock");

    /// <summary>A run's socket in releases before per-run sockets: one per session.</summary>
    public static string Legacy(string root, Guid sessionId) => Path.Combine(root, $"{sessionId}.sock");

    /// <summary>Every run socket of the session that <paramref name="socket"/> (a run socket) is for, but that one.</summary>
    public static IEnumerable<string> OtherRuns(string socket)
    {
        var directory = Path.GetDirectoryName(socket)!;
        var name = Path.GetFileName(socket);
        var session = name[..Math.Max(0, name.IndexOf('.', StringComparison.Ordinal))];
        return session.Length == 0 || !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, $"{session}.*.sock").Where(path => path != socket);
    }
}
