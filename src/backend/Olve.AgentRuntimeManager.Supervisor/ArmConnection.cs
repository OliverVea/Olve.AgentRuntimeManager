using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// The attached ARM: messages to it are queued (so a slow reader never holds up the agent's
/// output) and written in order; messages from it go to the <see cref="AgentSupervisor"/>.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ArmConnection(Socket socket, AgentSupervisor supervisor, SupervisorLog log) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Channel<SupervisorMessage> _outgoing = Channel.CreateUnbounded<SupervisorMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task _writing = Task.CompletedTask;

    /// <summary>Attached: gets the output as it comes. Set under the supervisor's lock.</summary>
    public bool Live { get; set; }

    public void Start(SupervisorMessage hello)
    {
        Send(hello);
        _writing = WriteAsync();
        _ = ReadAsync();
    }

    public void Send(SupervisorMessage message) => _outgoing.Writer.TryWrite(message);

    /// <summary>Waits until everything queued is written (or <paramref name="timeout"/> passes).</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        _outgoing.Writer.TryComplete();
        try
        {
            await _writing.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            log.Write("ARM did not read the end of the output in time");
        }
    }

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        _stop.Cancel();
        socket.Dispose();
    }

    private async Task WriteAsync()
    {
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_stop.Token))
            {
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message, SupervisorJsonContext.Default.SupervisorMessage), _stop.Token);
                stream.WriteByte((byte)'\n');
                await stream.FlushAsync(_stop.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // ARM went away; the files keep everything.
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Utf8);
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                SupervisorMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize(line, SupervisorJsonContext.Default.SupervisorMessage);
                }
                catch (JsonException exception)
                {
                    log.Write($"unreadable message from ARM: {exception.Message}");
                    continue;
                }

                if (message is not null)
                {
                    await supervisor.HandleAsync(this, message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // ARM went away.
        }
    }
}
