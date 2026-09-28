using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>
/// Starts per-session supervisors and finds them again after a restart (docs/GENTLE-RESTART.md).
/// Provider-agnostic: a provider says what to run; the supervisor runs exactly that.
/// </summary>
/// <remarks>
/// Disposing it (the server stopping) detaches from every supervisor it follows and leaves their
/// agents running, for the next server to find.
/// </remarks>
public sealed class Supervisors(IOptions<SupervisorOptions> options, ILogger<Supervisors> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<SupervisedAgent> _following = [];

    /// <summary>Environment the supervisor itself gets: only what a framework-dependent build needs to find .NET.</summary>
    private static readonly string[] SupervisorEnvironment = ["DOTNET_ROOT", "DOTNET_ROOT_X64"];

    /// <summary>
    /// Starts a supervisor running <paramref name="command"/> for a run, with <paramref name="input"/>
    /// as the agent's first input. Doesn't wait for it: the launch goes to its stdin (a small pipe
    /// write), and the returned agent connects in the background.
    /// </summary>
    public SupervisedAgent Launch(Guid sessionId, Guid runId, string folder, string workingDirectory,
        string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> input)
    {
        var settings = options.Value;
        var socketPath = SocketPath(sessionId);
        var launch = new SupervisorLaunch
        {
            Version = SupervisorProtocol.Version,
            RunId = runId,
            Command = command,
            Arguments = arguments,
            Environment = environment,
            WorkingDirectory = workingDirectory,
            Input = input,
            Folder = folder,
            SocketPath = socketPath,
        };

        var info = new ProcessStartInfo(Command(settings))
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        info.Environment.Clear();
        foreach (var name in SupervisorEnvironment)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                info.Environment[name] = value;
            }
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"The supervisor '{info.FileName}' did not start.");
        try
        {
            process.StandardInput.Write(JsonSerializer.Serialize(launch, SupervisorJsonContext.Default.SupervisorLaunch));
            process.StandardInput.Close();
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"The supervisor '{info.FileName}' exited before taking its launch.", exception);
        }

        // Kept until it exits, so it's reaped; it outlives this server if need be.
        _ = process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        logger.LogInformation("Session {SessionId}: supervisor {Pid} started for run {RunId}", sessionId, process.Id, runId);
        return Follow(new SupervisedAgent(socketPath, folder, runId, settings.ConnectTimeout));
    }

    /// <summary>Follows a run whose supervisor was started earlier (by this server or the one before).</summary>
    public SupervisedAgent Attach(Guid sessionId, Guid runId, string folder) =>
        Follow(new SupervisedAgent(SocketPath(sessionId), folder, runId, options.Value.ConnectTimeout));

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var agent in _following)
            {
                agent.Detach();
            }

            _following.Clear();
        }
    }

    private SupervisedAgent Follow(SupervisedAgent agent)
    {
        lock (_gate)
        {
            _following.Add(agent);
        }

        _ = agent.Exit.ContinueWith(_ =>
        {
            lock (_gate)
            {
                _following.Remove(agent);
            }
        }, TaskScheduler.Default);
        return agent;
    }

    /// <summary>Whether a supervisor answers on the session's socket.</summary>
    public bool IsListening(Guid sessionId)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath(sessionId)));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Kills what's left of a run whose supervisor is gone: its agent, if the pid still names the
    /// same process, together with the process group it ran in. Two agents on one provider session
    /// must never run.
    /// </summary>
    public void KillOrphan(SupervisorInfo info)
    {
        if (Posix.StartTime(info.AgentPid) is not { } startTime || startTime != info.AgentStartTime)
        {
            return;
        }

        logger.LogWarning("Run {RunId}: killing its orphaned agent {Pid}", info.RunId, info.AgentPid);
        Posix.Kill(-info.SupervisorPid, Posix.SigKill);
        Posix.Kill(info.AgentPid, Posix.SigKill);
    }

    public string SocketPath(Guid sessionId) => Path.Combine(SocketRoot(options.Value), $"{sessionId}.sock");

    private static string Command(SupervisorOptions settings) =>
        settings.Command is { Length: > 0 } command ? command : Path.Combine(AppContext.BaseDirectory, "olve-arm-supervisor");

    private static string SocketRoot(SupervisorOptions settings) =>
        settings.SocketRoot is { Length: > 0 } root ? root
        : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? Path.Combine(runtime, "olve-arm")
        : Path.Combine(Path.GetTempPath(), $"olve-arm-{Environment.UserName}");
}
