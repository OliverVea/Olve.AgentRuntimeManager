using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Runs one agent and holds its stdio for ARM: its stdout goes to <c>output.jsonl</c> (raw lines)
/// and to the attached ARM, its stderr to <c>stderr.log</c>, ARM's <c>write</c>s to its stdin.
/// The agent's input stays open while no ARM is attached (an agent waiting for an answer just
/// waits). When the agent has ended, <c>exit.json</c> is written and the supervisor exits.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class AgentSupervisor(SupervisorLaunch launch)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Guards the attached connection, the output's end and the exit, so output is sent in order, once.</summary>
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly SupervisorLog _log = new(Path.Combine(launch.Folder, SupervisorFiles.Log));
    private readonly string _outputPath = Path.Combine(launch.Folder, SupervisorFiles.Output);

    private Process _agent = null!;
    private SupervisorMessage _hello = null!;
    private ArmConnection? _connection;
    private long _outputStart;
    private long _outputEnd;
    private SupervisorMessage? _exited;
    private bool _inputClosed;

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(launch.Folder);
        await using var output = new FileStream(_outputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using var stderr = new FileStream(Path.Combine(launch.Folder, SupervisorFiles.Stderr), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _outputStart = _outputEnd = output.Position;

        try
        {
            _agent = Process.Start(StartInfo()) ?? throw new InvalidOperationException($"'{launch.Command}' did not start.");
        }
        catch (Exception exception)
        {
            _log.Write($"run {launch.RunId}: could not start the agent: {exception.Message}");
            WriteExit(127, exception.Message);
            return 1;
        }

        _log.Write($"run {launch.RunId}: agent {_agent.Id} started");
        var info = new SupervisorInfo
        {
            Version = SupervisorProtocol.Version,
            RunId = launch.RunId,
            SupervisorPid = Environment.ProcessId,
            AgentPid = _agent.Id,
            AgentStartTime = Posix.StartTime(_agent.Id),
            OutputStart = _outputStart,
            StderrStart = stderr.Position,
            StartedAt = DateTimeOffset.UtcNow,
        };
        SupervisorFiles.WriteAtomically(Path.Combine(launch.Folder, SupervisorFiles.Info), info, SupervisorJsonContext.Default.SupervisorInfo);
        _hello = new SupervisorMessage
        {
            Type = SupervisorMessage.Hello, Version = SupervisorProtocol.Version, RunId = launch.RunId,
            SupervisorPid = info.SupervisorPid, AgentPid = info.AgentPid, OutputStart = info.OutputStart, StderrStart = info.StderrStart,
        };

        var stdoutPump = PumpOutputAsync(output);
        var stderrPump = PumpStderrAsync(stderr);
        using var stop = new CancellationTokenSource();
        using var listener = Listen();
        var accepting = AcceptAsync(listener, stop.Token);
        // After the pumps and the socket: an agent that writes before it has read a large prompt
        // must not block this (nor keep ARM from connecting).
        var input = WriteInitialInputAsync();

        await Task.WhenAll(stdoutPump, stderrPump, input);
        await _agent.WaitForExitAsync();
        var exitCode = _agent.ExitCode;
        WriteExit(exitCode, error: null);
        _log.Write($"run {launch.RunId}: agent exited with {exitCode}");

        ArmConnection? connection;
        lock (_gate)
        {
            _exited = new SupervisorMessage { Type = SupervisorMessage.Exited, RunId = launch.RunId, ExitCode = exitCode };
            connection = _connection;
            if (connection is { Live: true })
            {
                connection.Send(_exited);
            }
        }

        // The files are the record from here on; an attached ARM gets the rest of the stream first.
        if (connection is { Live: true })
        {
            await connection.DrainAsync(DrainTimeout);
        }

        await stop.CancelAsync();
        listener.Dispose();
        File.Delete(launch.SocketPath);
        await accepting;
        connection?.Dispose();
        return 0;
    }

    /// <summary>A message from the attached ARM.</summary>
    internal async Task HandleAsync(ArmConnection connection, SupervisorMessage message)
    {
        switch (message.Type)
        {
            case SupervisorMessage.Attach:
                Attach(connection, message.Offset ?? _outputStart);
                break;
            case SupervisorMessage.Write when message.Line is { } line:
                await WriteInputAsync(line);
                break;
            case SupervisorMessage.CloseInput:
                await CloseInputAsync();
                break;
            case SupervisorMessage.Kill:
                Kill();
                break;
            default:
                // A newer ARM's message: ignored (the protocol only grows).
                break;
        }
    }

    private ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(launch.Command)
        {
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
        };
        foreach (var argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Exactly what ARM says: nothing of the supervisor's own environment.
        info.Environment.Clear();
        foreach (var (name, value) in launch.Environment)
        {
            info.Environment[name] = value;
        }

        return info;
    }

    private Socket Listen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(launch.SocketPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Delete(launch.SocketPath);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(launch.SocketPath));
        File.SetUnixFileMode(launch.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(4);
        return listener;
    }

    /// <summary>One ARM at a time: a new connection replaces the old one (that ARM is gone).</summary>
    private async Task AcceptAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            var connection = new ArmConnection(socket, this, _log);
            ArmConnection? previous;
            lock (_gate)
            {
                previous = _connection;
                _connection = connection;
            }

            previous?.Dispose();
            _log.Write($"run {launch.RunId}: ARM attached");
            connection.Start(_hello);
        }
    }

    /// <summary>Sends the output from <paramref name="offset"/> on, then every new line as it comes.</summary>
    private void Attach(ArmConnection connection, long offset)
    {
        lock (_gate)
        {
            if (connection != _connection)
            {
                return;
            }

            var position = Math.Clamp(offset, _outputStart, _outputEnd);
            if (position < _outputEnd)
            {
                using var file = new FileStream(_outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Seek(position, SeekOrigin.Begin);
                var bytes = new byte[_outputEnd - position];
                file.ReadExactly(bytes);
                var start = 0;
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != (byte)'\n')
                    {
                        continue;
                    }

                    connection.Send(new SupervisorMessage
                    {
                        Type = SupervisorMessage.Output, Line = Utf8.GetString(bytes, start, i - start), End = position + i + 1,
                    });
                    start = i + 1;
                }
            }

            connection.Live = true;
            if (_exited is { } exited)
            {
                connection.Send(exited);
            }
        }
    }

    private async Task PumpOutputAsync(FileStream output)
    {
        while (await _agent.StandardOutput.ReadLineAsync() is { } line)
        {
            var bytes = Utf8.GetBytes(line + "\n");
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            lock (_gate)
            {
                _outputEnd += bytes.Length;
                if (_connection is { Live: true } connection)
                {
                    connection.Send(new SupervisorMessage { Type = SupervisorMessage.Output, Line = line, End = _outputEnd });
                }
            }
        }
    }

    private async Task PumpStderrAsync(FileStream stderr)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await _agent.StandardError.ReadAsync(buffer)) > 0)
        {
            await stderr.WriteAsync(Utf8.GetBytes(buffer, 0, read));
            await stderr.FlushAsync();
        }
    }

    private async Task WriteInitialInputAsync()
    {
        foreach (var line in launch.Input)
        {
            await WriteInputAsync(line);
        }
    }

    private async Task WriteInputAsync(string line)
    {
        await _input.WaitAsync();
        try
        {
            if (_inputClosed)
            {
                return;
            }

            await _agent.StandardInput.WriteAsync(line + "\n");
            await _agent.StandardInput.FlushAsync();
        }
        catch (IOException)
        {
            // The agent is gone or closed its input; its exit tells why.
        }
        finally
        {
            _input.Release();
        }
    }

    private async Task CloseInputAsync()
    {
        await _input.WaitAsync();
        try
        {
            if (!_inputClosed)
            {
                _inputClosed = true;
                _agent.StandardInput.Close();
            }
        }
        catch (IOException)
        {
            // Already closed by the agent.
        }
        finally
        {
            _input.Release();
        }
    }

    private void Kill()
    {
        _log.Write($"run {launch.RunId}: killing the agent");
        try
        {
            _agent.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private void WriteExit(int exitCode, string? error) =>
        SupervisorFiles.WriteAtomically(
            Path.Combine(launch.Folder, SupervisorFiles.Exit),
            new SupervisorExit { RunId = launch.RunId, ExitCode = exitCode, Error = error, EndedAt = DateTimeOffset.UtcNow },
            SupervisorJsonContext.Default.SupervisorExit);
}
