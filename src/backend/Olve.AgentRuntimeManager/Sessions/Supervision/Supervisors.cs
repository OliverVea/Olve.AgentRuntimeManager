using System.Diagnostics;
using System.Globalization;
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

    /// <summary>The configured agent user's account, looked up once (null: agents run as ARM's own user).</summary>
    private readonly Lazy<AgentAccount?> _account = new(() => AgentAccount.Find(options.Value.User));

    /// <summary>
    /// Starts a supervisor running <paramref name="command"/> for a run in <paramref name="workingDirectory"/>
    /// (made here if it's new, or with an agent user by the supervisor, so it's that user's), with <paramref name="input"/>
    /// as the agent's first input. Doesn't wait for it: the launch goes to its stdin (a small pipe
    /// write), and the returned agent connects in the background.
    /// </summary>
    public SupervisedAgent Launch(Guid sessionId, Guid runId, string folder, string workingDirectory,
        string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> input)
    {
        var settings = options.Value;
        var socketPath = SocketPath(sessionId);
        var account = _account.Value;
        if (account is null)
        {
            Directory.CreateDirectory(workingDirectory);
        }
        else
        {
            // The supervisor writes the session's files and the agent its work: both as the agent user.
            // A new workplace the supervisor makes itself; one from before the agent user is ARM's.
            ShareWithAgent(folder);
            if (Directory.Exists(workingDirectory))
            {
                ShareWithAgent(workingDirectory);
            }
        }

        var launch = new SupervisorLaunch
        {
            Version = SupervisorProtocol.Version,
            RunId = runId,
            Command = command,
            Arguments = arguments,
            Environment = account is null ? environment : account.Identify(environment),
            WorkingDirectory = workingDirectory,
            Input = input,
            Folder = folder,
            SocketPath = socketPath,
            ClientUid = Posix.GetEUid(),
        };

        var info = StartInfo(settings, account, [Command(settings)]);
        info.WorkingDirectory = folder;
        info.RedirectStandardInput = true;
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
    /// <remarks>
    /// <paramref name="info"/> is <c>supervisor.json</c>, which an agent running as its own user can
    /// write (docs/AGENT-USER.md): so no pid of 1 or less (0 is this process's group, -1 every
    /// process of this user), and with an agent user the signals are sent as that user only, so
    /// whatever pids it names, nothing of ARM's user is hit.
    /// </remarks>
    public void KillOrphan(SupervisorInfo info)
    {
        if (info.SupervisorPid <= 1 || info.AgentPid <= 1)
        {
            logger.LogWarning("Run {RunId}: its supervisor.json names no process to kill ({SupervisorPid}, {AgentPid})",
                info.RunId, info.SupervisorPid, info.AgentPid);
            return;
        }

        if (Posix.StartTime(info.AgentPid) is not { } startTime || startTime != info.AgentStartTime)
        {
            return;
        }

        logger.LogWarning("Run {RunId}: killing its orphaned agent {Pid}", info.RunId, info.AgentPid);
        if (_account.Value is { } account)
        {
            // Only that user can signal its processes, so the supervisor does it as that user.
            KillAsAgentUser(account, info);
            return;
        }

        Posix.Kill(-info.SupervisorPid, Posix.SigKill);
        Posix.Kill(info.AgentPid, Posix.SigKill);
    }

    private void KillAsAgentUser(AgentAccount account, SupervisorInfo info)
    {
        var settings = options.Value;
        var start = StartInfo(settings, account,
            [Command(settings), "kill", info.SupervisorPid.ToString(CultureInfo.InvariantCulture), info.AgentPid.ToString(CultureInfo.InvariantCulture)]);
        start.RedirectStandardError = true;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"'{start.FileName}' did not start.");
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(settings.ConnectTimeout))
            {
                process.Kill();
                logger.LogError("Run {RunId}: killing its orphaned agent as {User} timed out", info.RunId, account.Name);
            }
            else if (process.ExitCode != 0)
            {
                logger.LogError("Run {RunId}: killing its orphaned agent as {User} failed ({ExitCode}): {Error}",
                    info.RunId, account.Name, process.ExitCode, error.Result.Trim());
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(exception, "Run {RunId}: could not kill its orphaned agent as {User}", info.RunId, account.Name);
        }
    }

    /// <summary>
    /// How the supervisor binary is started with <paramref name="arguments"/> (the binary first):
    /// directly, or as the agent user through <c>sudo -n -u &lt;user&gt; --</c> (non-interactive:
    /// a missing sudoers rule fails at once instead of waiting for a password).
    /// </summary>
    private static ProcessStartInfo StartInfo(SupervisorOptions settings, AgentAccount? account, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(account is null ? arguments[0] : settings.Sudo) { UseShellExecute = false };
        if (account is not null)
        {
            foreach (var argument in (string[])["-n", "-u", account.Name, "--", arguments[0]])
            {
                info.ArgumentList.Add(argument);
            }
        }

        foreach (var argument in arguments.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    /// Lets the agent user's group (which ARM is in) write <paramref name="directory"/>, and what's
    /// created in it inherit the group (setgid); the group itself comes from the work root's setgid.
    /// </summary>
    private static void ShareWithAgent(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The agent can write the session's folder: a link it put there must not make ARM share its
        // target. Opened without following one, and changed through that handle (no check-then-use).
        using var handle = Posix.OpenNoFollow(directory, directory: true)
            ?? throw new InvalidOperationException($"'{directory}' is a link or missing, not the session's own folder.");
        const UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
        var mode = File.GetUnixFileMode(handle);
        if ((mode & shared) != shared)
        {
            try
            {
                File.SetUnixFileMode(handle, mode | shared);
            }
            catch (UnauthorizedAccessException)
            {
                // Not ARM's: a workplace the agent's supervisor made, and the agent changed. Its own business.
            }
        }
    }

    public string SocketPath(Guid sessionId) => Path.Combine(SocketRoot(options.Value), $"{sessionId}.sock");

    private static string Command(SupervisorOptions settings) =>
        settings.Command is { Length: > 0 } command ? command : Path.Combine(AppContext.BaseDirectory, "olve-arm-supervisor");

    private static string SocketRoot(SupervisorOptions settings) =>
        settings.SocketRoot is { Length: > 0 } root ? root
        : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? Path.Combine(runtime, "olve-arm")
        : Path.Combine(Path.GetTempPath(), $"olve-arm-{Environment.UserName}");
}
