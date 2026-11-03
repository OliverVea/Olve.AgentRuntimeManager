using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.Server;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.External;

/// <summary>
/// One external MCP server over stdio, with this process as its client: JSON-RPC 2.0, one message
/// per line. It runs in a session (and group) of its own, in the workplace, with the agent's
/// environment and its registered variables on top. Clients of it never see it restart: once it
/// ends, every call waiting and every later one fails with why.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ExternalServer(McpServerDefinition definition, string workplace, ProcessGroups groups, TextWriter log)
{
    /// <summary>
    /// Claude Code keeps it from its MCP servers (checked on 2.1.316, 2026-11-02); that's its
    /// behaviour and not a promise, and a package from npm has no business with the login.
    /// </summary>
    private const string LoginToken = "CLAUDE_CODE_OAUTH_TOKEN";

    private const int MethodNotFound = -32601;
    private const int MaxToolPages = 100;

    /// <summary>How much of what it wrote to stderr is kept, for the error when it fails.</summary>
    private const int MaxStderr = 2000;

    /// <summary>How long its last output may still arrive after it has exited.</summary>
    private static readonly TimeSpan Drain = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the rest of its output may take once it has exited (as <c>arm_bash</c>'s).</summary>
    private static readonly TimeSpan ReadersWait = TimeSpan.FromSeconds(5);

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _waiting = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private long _lastId;
    private Process? _process;
    private int _group;
    private Task _watching = Task.CompletedTask;

    private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Why it ended, once it has.</summary>
    private volatile string? _ended;

    public string Name => definition.Name;

    /// <summary>The end of what it wrote to stderr, as a line to add to an error; empty if nothing.</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderr)
            {
                var text = _stderr.ToString().Trim();
                return text.Length == 0 ? "" : $"\n{text}";
            }
        }
    }

    /// <summary>Starts the process; a server that can't be found ends at once, as <c>setsid</c>'s exit.</summary>
    public void Start()
    {
        var info = new ProcessStartInfo("setsid")
        {
            WorkingDirectory = workplace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["--", definition.Command, .. definition.Args])
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(LoginToken);
        foreach (var (name, value) in definition.Env)
        {
            info.Environment[name] = value;
        }

        try
        {
            var process = groups.StartService(info);
            _process = process;
            _group = process.Id;
            _watching = Task.Run(() => WatchAsync(process));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ExternalServerFailure(exception.Message);
        }
    }

    /// <summary>The handshake and the list of tools; throws <see cref="ExternalServerFailure"/>.</summary>
    public async Task<IReadOnlyList<ExternalTool>> ConnectAsync(CancellationToken cancellationToken)
    {
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = McpServer.ProtocolVersions[0],
            // None: it may not ask this client for roots, sampling or elicitation.
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "olve-arm-tools",
                ["version"] = typeof(ExternalServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
            },
        }, cancellationToken);
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, cancellationToken);

        var tools = new List<ExternalTool>();
        var realNames = new Dictionary<string, string>();
        string? cursor = null;
        for (var page = 0; page < MaxToolPages; page++)
        {
            var list = await RequestAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, cancellationToken);
            foreach (var tool in list["tools"] as JsonArray ?? new JsonArray())
            {
                tools.Add(Export(tool, realNames));
            }

            cursor = list["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var text) ? text : null;
            if (cursor is null)
            {
                return tools;
            }
        }

        throw new ExternalServerFailure($"its tools run past {MaxToolPages} pages.");
    }

    /// <summary>A <c>tools/call</c>'s result as it came; throws <see cref="ExternalServerFailure"/>, or cancels.</summary>
    public async Task<JsonObject> CallAsync(string tool, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["name"] = tool, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() };
        try
        {
            return await RequestAsync("tools/call", parameters, cancellationToken);
        }
        catch (ExternalServerFailure failure)
        {
            throw new ExternalServerFailure(_ended is { } ended
                ? $"MCP server '{Name}' has stopped ({ended}): the call got no answer, and its tools can't be used again in this session.{StderrTail}"
                : $"MCP server '{Name}' refused the call: {failure.Message}");
        }
    }

    /// <summary>Stops it and everything in its group: end of input, SIGTERM, then SIGKILL.</summary>
    public async Task StopAsync()
    {
        // Ended by itself, its group is gone and its id isn't ours to signal.
        if (_process is not { } process || _watching.IsCompleted)
        {
            await _watching;
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Ended already.
        }

        await groups.StopAsync(_group);
        await _watching;
    }

    private ExternalTool Export(JsonNode? tool, Dictionary<string, string> realNames)
    {
        if (tool is not JsonObject definition || definition["name"] is not JsonValue value || !value.TryGetValue<string>(out var name) || name.Length == 0)
        {
            throw new ExternalServerFailure("it listed a tool without a name.");
        }

        var exported = ExternalToolNames.Export(Name, name);
        if (ExternalToolNames.Problem(name, exported) is { } problem)
        {
            throw new ExternalServerFailure(problem);
        }

        if (!realNames.TryAdd(exported, name))
        {
            throw new ExternalServerFailure($"its tools '{realNames[exported]}' and '{name}' would both be called {exported}.");
        }

        var shown = definition.DeepClone().AsObject();
        shown["name"] = exported;
        // A large result loses its structuredContent (ToolOutputs), and a client refuses a result
        // that a tool's output schema promised structured.
        shown.Remove("outputSchema");
        return new ExternalTool(exported, name, this, shown);
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _lastId);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        JsonObject message;
        try
        {
            // After it's waiting: an end between the two fails it either way.
            if (_ended is { } ended)
            {
                throw new ExternalServerFailure(ended);
            }

            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken);
            message = await answer.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (method == "tools/call")
        {
            await TellCancelledAsync(id);
            throw;
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }

        if (message.TryGetPropertyValue("error", out var error) && error is not null)
        {
            throw new ExternalServerFailure(error["message"] is JsonValue text && text.TryGetValue<string>(out var words) ? words : error.ToJsonString());
        }

        return message["result"]?.DeepClone() as JsonObject ?? throw new ExternalServerFailure($"its answer to {method} held no result.");
    }

    /// <summary>Tells it a call was cancelled, if that can be done at once: nothing waits on a server that isn't reading.</summary>
    private async Task TellCancelledAsync(long id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/cancelled",
                ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "The agent's call was cancelled." },
            }, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ExternalServerFailure)
        {
            await log.WriteLineAsync($"olve-arm-tools: could not tell MCP server '{Name}' of a cancelled call: {exception.Message}");
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(ToolResult.Json));
        string closed;
        await _writing.WaitAsync(cancellationToken);
        try
        {
            var input = _process!.StandardInput.BaseStream;
            await input.WriteAsync(bytes, cancellationToken);
            await input.WriteAsync(NewLine, cancellationToken);
            await input.FlushAsync(cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            closed = exception.Message;
        }
        finally
        {
            _writing.Release();
        }

        // Most likely it has just ended, and why says more than a broken pipe does.
        await Task.WhenAny(_end.Task, Task.Delay(Drain * 2, cancellationToken));
        throw new ExternalServerFailure(_ended ?? $"its input is closed: {closed}");
    }

    /// <summary>Reads it until it ends, then fails whatever waits on it and stops what's left of its group.</summary>
    private async Task WatchAsync(Process process)
    {
        var group = process.Id;
        using (process)
        {
            var reading = ReadAsync(process.StandardOutput);
            var errors = KeepStderrAsync(process.StandardError);
            var exiting = process.WaitForExitAsync();
            await Task.WhenAny(reading, exiting);
            // Its output closing and its exit come together, in either order; the reason needs both.
            var all = Task.WhenAll(reading, errors, exiting);
            await Task.WhenAny(all, Task.Delay(Drain));
            // It has exited, so its pipes are closing: a loaded machine may take a while to read the
            // rest of what it said (its stderr is the reason a start failed). Only a process it left
            // running that holds them waits this out.
            if (process.HasExited && !all.IsCompleted)
            {
                await Task.WhenAny(all, Task.Delay(ReadersWait));
            }

            var reason = process.HasExited
                ? string.Create(CultureInfo.InvariantCulture, $"it exited with code {process.ExitCode}")
                : "it closed its output";
            End(reason);
            await groups.StopAsync(group);
            groups.RemoveService(group);
        }
    }

    private void End(string reason)
    {
        _ended = reason;
        _end.TrySetResult();
        log.WriteLine($"olve-arm-tools: MCP server '{Name}' ended: {reason}.");
        foreach (var (_, waiting) in _waiting)
        {
            waiting.TrySetException(new ExternalServerFailure(reason));
        }
    }

    private async Task ReadAsync(StreamReader output)
    {
        try
        {
            while (await output.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    await HandleSafelyAsync(line);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe closed under us: that's its end.
        }
    }

    /// <summary>One line, whatever it holds: stdout that isn't a message (a banner) ends nothing.</summary>
    private async Task HandleSafelyAsync(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject message)
            {
                await HandleAsync(message);
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException or ExternalServerFailure)
        {
            await log.WriteLineAsync($"olve-arm-tools: MCP server '{Name}' wrote what isn't a message ({exception.GetType().Name}).");
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var isRequest = message.TryGetPropertyValue("id", out var id) && id is not null;
        if (message["method"] is JsonValue method && method.TryGetValue<string>(out var name))
        {
            // Notifications (a changed list, progress, logging) are left unread.
            if (isRequest)
            {
                await AnswerAsync(id, name);
            }
        }
        else if (isRequest && id is JsonValue value && value.TryGetValue<long>(out var number) && _waiting.TryGetValue(number, out var waiting))
        {
            waiting.TrySetResult(message);
        }
    }

    /// <summary>What a server asks its client: only <c>ping</c> is for any client to answer; this one declared nothing else.</summary>
    private Task AnswerAsync(JsonNode? id, string method) =>
        SendAsync(method == "ping"
            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = new JsonObject() }
            : new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = MethodNotFound, ["message"] = $"Method not found: {method}" },
            }, CancellationToken.None);

    /// <summary>Reads stderr all along (a full pipe would stall the server), keeping only its end.</summary>
    private async Task KeepStderrAsync(StreamReader errors)
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await errors.ReadAsync(buffer)) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > MaxStderr)
                    {
                        _stderr.Remove(0, _stderr.Length - MaxStderr);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe closed under us: what was read is the tail.
        }
    }
}
