using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.Protocol;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Server;

/// <summary>
/// The MCP server over stdio: JSON-RPC 2.0, one message per line on stdin and stdout (stdout is
/// for messages only; logs go to stderr). Input is read on while calls run, each call on its own,
/// so a long <c>arm_bash</c> holds up nothing and can be cancelled.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class McpServer(ToolsOptions options, ToolDispatcher dispatcher, ProcessGroups groups, Stream input, Stream output, TextWriter log)
{
    /// <summary>The MCP versions spoken, newest first: a client's is answered with its own if it's here, else the newest.</summary>
    public static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>How long a stop waits for the calls to stop, beyond their own SIGTERM grace.</summary>
    private static readonly TimeSpan StopWait = ProcessGroups.Grace + TimeSpan.FromSeconds(2);

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    /// <summary>Only what JSON needs escaped: the agent's text (quotes, backticks, non-ASCII) stays as it is, and shorter.</summary>
    private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancels = new();
    private readonly ConcurrentDictionary<Task, bool> _calls = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Held while a call is started or the stop begins: no call starts once the stop has.</summary>
    private readonly Lock _gate = new();
    private bool _stopped;

    /// <summary>Reads and answers messages until the input ends (or the server stops).</summary>
    public async Task ReadAsync()
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        while (!_stopping.IsCancellationRequested && await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                await HandleSafelyAsync(line);
            }
        }
    }

    /// <summary>
    /// Stops every running call (its process group: SIGTERM, then SIGKILL), answering none of them:
    /// they stay started in the journal, so they read as interrupted. Once; later calls return at once.
    /// </summary>
    public async Task StopAsync(string reason)
    {
        Task[] calls;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            calls = [.. _calls.Keys];
        }

        await log.WriteLineAsync($"olve-arm-tools: stopping ({reason}), {calls.Length} call(s) running.");
        await _stopping.CancelAsync();
        await Task.WhenAny(Task.WhenAll(calls), Task.Delay(StopWait));
        // Whatever didn't go in time, and anything left below the server.
        groups.KillAll(Posix.SigKill);
        await groups.StopOrphansAsync(TimeSpan.FromMilliseconds(500), stopping: true);
    }

    /// <summary>
    /// One message, whatever it holds: a line the server can't make sense of (a duplicate key, a
    /// lone surrogate, a field of the wrong kind) is answered as an invalid request if it has an
    /// id to answer, logged otherwise, and the server reads on. No line ends it.
    /// </summary>
    private async Task HandleSafelyAsync(string line)
    {
        try
        {
            await HandleAsync(line);
        }
        catch (Exception exception)
        {
            await log.WriteLineAsync($"olve-arm-tools: an unreadable message ({exception.GetType().Name}: {exception.Message}).");
            // A request is answered, with its id if that can be read (null otherwise, as JSON-RPC has it).
            if (IdOf(line) is (true, var id))
            {
                await SendErrorAsync(id, InvalidRequest, $"Unreadable message: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Whether a message the server couldn't handle has an id (it's a request), and the id if it can
    /// be read (a number, or a string that is valid text).
    /// </summary>
    private static (bool IsRequest, JsonNode? Id) IdOf(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return (false, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("id", out var id))
            {
                return (false, null);
            }

            try
            {
                return (true, id.ValueKind switch
                {
                    JsonValueKind.Number => JsonNode.Parse(id.GetRawText()),
                    JsonValueKind.String => JsonValue.Create(id.GetString()),
                    _ => null,
                });
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
            {
                return (true, null);
            }
        }
    }

    private async Task HandleAsync(string line)
    {
        JsonObject message;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject parsed)
            {
                await SendErrorAsync(null, InvalidRequest, "Expected a JSON-RPC message (an object).");
                return;
            }

            message = parsed;
        }
        catch (JsonException exception)
        {
            await SendErrorAsync(null, ParseError, $"Not JSON: {exception.Message}");
            return;
        }

        var id = message["id"];
        var isRequest = message.ContainsKey("id");
        if (message["method"] is not JsonValue methodValue || methodValue.GetValueKind() != JsonValueKind.String)
        {
            // A response (the server asks the client nothing), or no message at all.
            if (isRequest && !message.ContainsKey("result") && !message.ContainsKey("error"))
            {
                await SendErrorAsync(id, InvalidRequest, "A request needs a method.");
            }

            return;
        }

        var method = methodValue.GetValue<string>();
        var parameters = message["params"] as JsonObject;
        switch (method)
        {
            case "initialize" when isRequest:
                await SendResultAsync(id, Initialize(parameters));
                break;
            case "ping" when isRequest:
                await SendResultAsync(id, new JsonObject());
                break;
            case "tools/list" when isRequest:
                await SendResultAsync(id, new JsonObject { ["tools"] = ToolDefinitions.List(options.Tools) });
                break;
            case "tools/call" when isRequest:
                await StartCallAsync(id, parameters);
                break;
            case "notifications/cancelled":
                Cancel(parameters);
                break;
            default:
                // Anything else (Claude Code asks `server/discover` first, then initializes when
                // it's not known); notifications (`notifications/initialized`) are never answered.
                if (isRequest)
                {
                    await SendErrorAsync(id, MethodNotFound, $"Method not found: {method}");
                }

                break;
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var asked = parameters?["protocolVersion"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        var version = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ToolsLaunch.ServerName,
                ["title"] = "ARM's tools",
                ["version"] = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
            },
            ["instructions"] = $"ARM's tools for this session. Relative paths resolve from the workplace, {options.Workplace}, where arm_bash runs unless given a working_dir.",
        };
    }

    private async Task StartCallAsync(JsonNode? id, JsonObject? parameters)
    {
        if (parameters?["name"] is not JsonValue nameValue || nameValue.GetValueKind() != JsonValueKind.String)
        {
            await SendErrorAsync(id, InvalidParams, "tools/call needs params.name, the tool's name.");
            return;
        }

        if (parameters["arguments"] is not (null or JsonObject))
        {
            await SendErrorAsync(id, InvalidParams, "tools/call's params.arguments must be an object.");
            return;
        }

        var name = nameValue.GetValue<string>();
        var arguments = parameters["arguments"] as JsonObject;
        // A `_meta` that isn't an object holds no id: the call runs without one.
        var toolUseId = parameters["_meta"] is JsonObject meta && meta["claudecode/toolUseId"] is JsonValue useId && useId.GetValueKind() == JsonValueKind.String
            ? useId.GetValue<string>()
            : null;
        var key = Key(id);
        var cancel = new CancellationTokenSource();
        if (!_cancels.TryAdd(key, cancel))
        {
            cancel.Dispose();
            await SendErrorAsync(id, InvalidRequest, $"A request with id {key} is running already.");
            return;
        }

        lock (_gate)
        {
            if (!_stopped)
            {
                var call = Run();
                _calls[call] = true;
                _ = call.ContinueWith(done => _calls.TryRemove(done, out _), TaskScheduler.Default);
                return;
            }
        }

        // Stopping: nothing new starts (its process would outlive the stop).
        _cancels.TryRemove(key, out _);
        cancel.Dispose();
        await SendResultAsync(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The tools server is stopping: the call wasn't run." }),
            ["isError"] = true,
        });

        Task Run() => Task.Run(async () =>
        {
            try
            {
                await log.WriteLineAsync($"olve-arm-tools: {name} ({toolUseId ?? "no tool-use id"}) started.");
                var result = await dispatcher.CallAsync(name, arguments, toolUseId, cancel.Token, _stopping.Token);
                // Cancelled: the client wants no answer. Stopping: there's no one to answer.
                if (result is not null && !cancel.IsCancellationRequested && !_stopping.IsCancellationRequested)
                {
                    await SendResultAsync(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
                        ["isError"] = result.IsError,
                    });
                }

                await log.WriteLineAsync($"olve-arm-tools: {name} ({toolUseId ?? "no tool-use id"}) {(result is null ? "stopped" : result.IsError ? "failed" : "done")}.");
            }
            catch (Exception exception)
            {
                await log.WriteLineAsync($"olve-arm-tools: {name} threw: {exception}");
                if (!_stopping.IsCancellationRequested)
                {
                    await SendResultAsync(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"{name} failed inside the tools server: {exception.Message}" }),
                        ["isError"] = true,
                    });
                }
            }
            finally
            {
                _cancels.TryRemove(key, out _);
                cancel.Dispose();
            }
        });
    }

    private void Cancel(JsonObject? parameters)
    {
        if (parameters?["requestId"] is { } requestId && _cancels.TryGetValue(Key(requestId), out var cancel))
        {
            log.WriteLine($"olve-arm-tools: request {Key(requestId)} cancelled by the client.");
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }
    }

    private static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";

    private Task SendResultAsync(JsonNode? id, JsonObject result) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    private Task SendErrorAsync(JsonNode? id, int code, string text) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = text },
        });

    private async Task SendAsync(JsonObject message)
    {
        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Wire));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Text JSON can't hold (a lone surrogate, echoed from the request): answer that instead.
            await log.WriteLineAsync($"olve-arm-tools: could not encode an answer: {exception.Message}");
            try
            {
                bytes = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message["id"]?.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = InternalError, ["message"] = "The answer held text JSON can't encode." },
                }.ToJsonString(Wire));
            }
            catch (Exception again) when (again is ArgumentException or InvalidOperationException)
            {
                return;
            }
        }

        await _writing.WaitAsync();
        try
        {
            await output.WriteAsync(bytes);
            await output.WriteAsync(NewLine);
            await output.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The client is gone: its input ends next, which stops the server.
            await log.WriteLineAsync($"olve-arm-tools: could not answer: {exception.Message}");
        }
        finally
        {
            _writing.Release();
        }
    }
}
