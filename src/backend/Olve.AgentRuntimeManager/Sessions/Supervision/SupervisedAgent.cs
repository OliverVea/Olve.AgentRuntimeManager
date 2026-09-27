using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>
/// ARM's side of one supervised run: the agent's output lines (from the run's start, in order),
/// its input, and its exit. Connects to the supervisor in the background and reconnects if the
/// connection drops; once the supervisor is gone, the run's files finish the story (the rest of
/// <c>output.jsonl</c>, then <c>exit.json</c>). Commands given before it's connected are queued.
/// </summary>
public sealed class SupervisedAgent
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _socketPath;
    private readonly string _folder;
    private readonly Guid _runId;
    private readonly TimeSpan _connectTimeout;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<SupervisorMessage> _outgoing = Channel.CreateUnbounded<SupervisorMessage>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _detach = new();

    /// <summary>How far into <c>output.jsonl</c> the lines handed out so far go; null until known.</summary>
    private long? _offset;
    private long? _stderrStart;

    internal SupervisedAgent(string socketPath, string folder, Guid runId, TimeSpan connectTimeout)
    {
        _socketPath = socketPath;
        _folder = folder;
        _runId = runId;
        _connectTimeout = connectTimeout;
        _ = Task.Run(RunAsync);
    }

    /// <summary>The agent's exit code, once all its output has been read out; faults with <see cref="SupervisorLostException"/>.</summary>
    public Task<int> Exit => _exit.Task;

    /// <summary>The next line of the agent's output; null when there is no more.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        await _lines.Reader.WaitToReadAsync(cancellationToken) && _lines.Reader.TryRead(out var line) ? line : null;

    public void WriteLine(string line) => _outgoing.Writer.TryWrite(new SupervisorMessage { Type = SupervisorMessage.Write, Line = line });

    public void CloseInput() => _outgoing.Writer.TryWrite(new SupervisorMessage { Type = SupervisorMessage.CloseInput });

    public void Kill() => _outgoing.Writer.TryWrite(new SupervisorMessage { Type = SupervisorMessage.Kill });

    /// <summary>
    /// Lets go of the supervisor without stopping the agent, as a server that exits does: the
    /// connection closes, and <see cref="Exit"/> and the output never complete.
    /// </summary>
    public void Detach() => _detach.Cancel();

    /// <summary>The end of this run's stderr, at most <paramref name="length"/> characters.</summary>
    public string StderrTail(int length)
    {
        try
        {
            using var file = new FileStream(Path.Combine(_folder, SupervisorFiles.Stderr), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(_stderrStart ?? 0, file.Length - (length * 4L));
            file.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(file, Utf8);
            var text = reader.ReadToEnd();
            return text.Length > length ? text[^length..] : text;
        }
        catch (IOException)
        {
            return "";
        }
    }

    private async Task RunAsync()
    {
        var detached = _detach.Token;
        try
        {
            var deadline = DateTimeOffset.UtcNow + _connectTimeout;
            while (true)
            {
                if (await TryConnectAsync(detached) is { } socket)
                {
                    Followed followed;
                    using (socket)
                    {
                        followed = await FollowAsync(socket, detached);
                    }

                    if (followed == Followed.Exited)
                    {
                        return;
                    }

                    if (followed == Followed.Dropped)
                    {
                        // Dropped without the exit: the supervisor may be restarting its end, or gone.
                        deadline = DateTimeOffset.UtcNow + _connectTimeout;
                        continue;
                    }
                }

                if (ReadExit() is { } exit)
                {
                    FinishFromFiles(exit);
                    return;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    Fail($"The supervisor at {_socketPath} is gone and left no exit for run {_runId}.");
                    return;
                }

                await Task.Delay(50, detached);
            }
        }
        catch (OperationCanceledException) when (detached.IsCancellationRequested)
        {
            // Detached: the next server takes over.
        }
        catch (Exception exception)
        {
            Fail($"Following the supervisor failed: {exception.Message}");
        }
    }

    private async Task<Socket?> TryConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private enum Followed
    {
        Exited,
        Dropped,
        /// <summary>Another run's supervisor answered: this run's is gone.</summary>
        Foreign,
    }

    /// <summary>Reads the supervisor's stream until the agent's exit or the connection drops.</summary>
    private async Task<Followed> FollowAsync(Socket socket, CancellationToken detached)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Utf8);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(detached);
        Task? writing = null;
        try
        {
            while (await reader.ReadLineAsync(detached) is { } json)
            {
                var message = JsonSerializer.Deserialize(json, SupervisorJsonContext.Default.SupervisorMessage);
                switch (message?.Type)
                {
                    case SupervisorMessage.Hello:
                        if (message.RunId != _runId)
                        {
                            return Followed.Foreign;
                        }

                        _offset ??= message.OutputStart;
                        _stderrStart ??= message.StderrStart;
                        await SendAsync(stream, new SupervisorMessage { Type = SupervisorMessage.Attach, Offset = _offset }, detached);
                        writing = PumpOutgoingAsync(stream, stop.Token);
                        break;
                    case SupervisorMessage.Output when message.Line is { } line:
                        _offset = message.End;
                        _lines.Writer.TryWrite(line);
                        break;
                    case SupervisorMessage.Exited:
                        _lines.Writer.TryComplete();
                        _exit.TrySetResult(message.ExitCode ?? -1);
                        return Followed.Exited;
                }
            }

            return Followed.Dropped;
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return Followed.Dropped;
        }
        finally
        {
            await stop.CancelAsync();
            if (writing is not null)
            {
                await writing;
            }
        }
    }

    private async Task PumpOutgoingAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (await _outgoing.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_outgoing.Reader.TryPeek(out var message))
                {
                    await SendAsync(stream, message, cancellationToken);
                    _outgoing.Reader.TryRead(out _);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The connection ended; what's left is sent on the next one.
        }
    }

    private static async Task SendAsync(Stream stream, SupervisorMessage message, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message, SupervisorJsonContext.Default.SupervisorMessage), cancellationToken);
        stream.WriteByte((byte)'\n');
        await stream.FlushAsync(cancellationToken);
    }

    private SupervisorExit? ReadExit() => SupervisorFiles.ReadExit(_folder) is { } exit && exit.RunId == _runId ? exit : null;

    /// <summary>The supervisor has gone after the agent ended: the rest of the output from disk, then the exit.</summary>
    private void FinishFromFiles(SupervisorExit exit)
    {
        var info = SupervisorFiles.ReadInfo(_folder) is { } i && i.RunId == _runId ? i : null;
        _stderrStart ??= info?.StderrStart;
        var outputPath = Path.Combine(_folder, SupervisorFiles.Output);
        // No info: the agent never started, so it wrote nothing.
        if ((_offset ?? info?.OutputStart) is { } offset && File.Exists(outputPath))
        {
            foreach (var line in ReadLines(outputPath, offset))
            {
                _lines.Writer.TryWrite(line);
            }
        }

        _lines.Writer.TryComplete();
        _exit.TrySetResult(exit.ExitCode);
    }

    private void Fail(string error)
    {
        _lines.Writer.TryComplete();
        _exit.TrySetException(new SupervisorLostException(error));
    }

    /// <summary>The complete lines of a file from a byte offset on.</summary>
    internal static IEnumerable<string> ReadLines(string path, long offset)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(file, Utf8);
        var text = reader.ReadToEnd();
        var end = text.LastIndexOf('\n');
        return end < 0 ? [] : text[..end].Split('\n');
    }
}
