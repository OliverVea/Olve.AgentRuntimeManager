using System.Globalization;
using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.External;

/// <summary>
/// A session's external MCP servers (M10c): all started together, under one bound, and their tools
/// exported as the session's. One that won't start fails them all, at once, with its reason.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ExternalServers(string? file, string workplace, ProcessGroups groups, TextWriter log)
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ExternalServer> _servers = [];
    private List<ExternalTool> _tools = [];

    /// <summary>Whether every server is up, once that's known; a session with none is at once.</summary>
    public Task<bool> Ready => _ready.Task;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully && _ready.Task.Result;

    /// <summary>Every server's tools, in the session's order: empty until <see cref="Ready"/>.</summary>
    public IReadOnlyList<ExternalTool> Tools => _tools;

    public ExternalTool? Find(string name) => _tools.Find(tool => tool.Name == name);

    /// <summary>
    /// Starts every process before it first waits, then returns the session's error if they
    /// aren't all up in <paramref name="timeout"/>; null if they are, or the stop came first.
    /// </summary>
    public async Task<string?> StartAsync(TimeSpan timeout)
    {
        if (file is null)
        {
            _ready.SetResult(true);
            return null;
        }

        if (SupervisorFiles.ReadMcpServers(file) is not { } definitions)
        {
            return Failed($"The session's MCP servers can't be read from {file}.");
        }

        foreach (var definition in definitions)
        {
            var server = new ExternalServer(definition, workplace, groups, log);
            _servers.Add(server);
            try
            {
                server.Start();
            }
            catch (ExternalServerFailure failure)
            {
                return Failed($"MCP server '{server.Name}' did not start: {failure.Message}");
            }
        }

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        bound.CancelAfter(timeout);
        var connecting = _servers.Select(server => ConnectAsync(server, timeout, bound.Token)).ToList();
        var left = connecting.ToList();
        while (left.Count > 0)
        {
            var done = await Task.WhenAny(left);
            left.Remove(done);
            try
            {
                await done;
            }
            catch (OperationCanceledException)
            {
                _ready.TrySetResult(false);
                return null;
            }
            catch (ExternalServerFailure failure)
            {
                return Failed(failure.Message);
            }
        }

        _tools = [.. connecting.SelectMany(task => task.Result)];
        await log.WriteLineAsync($"olve-arm-tools: MCP servers up: {string.Join(", ", _servers.Select(server => server.Name))}; {_tools.Count} tool(s).");
        _ready.SetResult(true);
        return null;
    }

    /// <summary>Stops every server (and one still starting), together.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_servers.Select(server => server.StopAsync()));
    }

    private string Failed(string error)
    {
        _ready.TrySetResult(false);
        return error;
    }

    private async Task<IReadOnlyList<ExternalTool>> ConnectAsync(ExternalServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await server.ConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            throw new ExternalServerFailure(string.Create(CultureInfo.InvariantCulture,
                $"MCP server '{server.Name}' gave no answer in {timeout.TotalSeconds:0} s{server.StderrTail}"));
        }
        catch (ExternalServerFailure failure)
        {
            throw new ExternalServerFailure($"MCP server '{server.Name}' did not start: {failure.Message}{server.StderrTail}");
        }
    }
}
