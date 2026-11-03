using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Tools.External;
using Olve.AgentRuntimeManager.Tools.Journal;
using Olve.AgentRuntimeManager.Tools.Protocol;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// Runs a <c>tools/call</c>: only the tools the session named, each id at most once (the
/// <see cref="CallJournal"/>), a failure of any kind as a result the agent reads, and a large result
/// cut, with the whole of it in a file (<see cref="ToolOutputs"/>).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ToolDispatcher(ToolContext context, IReadOnlyCollection<string> tools, CallJournal journal, ExternalServers? externals = null, TimeProvider? time = null)
{
    /// <summary>
    /// How long an external MCP server's call may take. <c>arm_bash</c> has the same default and
    /// its own <c>timeout_seconds</c>; the file tools can't be cut short, so they have none.
    /// </summary>
    public static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(BashTool.DefaultTimeoutSeconds);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The call's result; null if the server's stop (<paramref name="stopping"/>) cut it short: it's
    /// then left as started in the journal, interrupted, and nothing is answered.
    /// </summary>
    public async Task<ToolResult?> CallAsync(string name, JsonObject? arguments, string? toolUseId, CancellationToken cancelled, CancellationToken stopping)
    {
        var external = externals?.Find(name);
        if (external is null && !tools.Contains(name))
        {
            string[] served = [.. ToolCatalog.All.Where(tool => tools.Contains(tool.Name)).Select(tool => tool.Name), .. externals?.Tools.Select(tool => tool.Name) ?? []];
            return ToolResult.Error($"There's no tool '{name}' in this session. Its tools: {(served.Length == 0 ? "none" : string.Join(", ", served))}.");
        }

        // Nothing starts once the server stops: what it started would outlive the stop.
        if (stopping.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            if (toolUseId is not null && journal.Begin(toolUseId, name) is { } recorded)
            {
                return recorded;
            }
        }
        catch (Exception exception)
        {
            // Not written down, so not run: it couldn't be kept from running twice.
            return ToolResult.Error($"{name} wasn't run: the journal can't be written ({exception.Message}).");
        }

        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancelled, stopping);
        var started = _time.GetTimestamp();
        ToolResult result;
        try
        {
            result = external is null
                ? await RunAsync(name, new ToolArguments(arguments), toolUseId, either.Token)
                : await RunExternalAsync(external, arguments, either.Token);
        }
        catch (Exception exception)
        {
            // Anything (a NUL in a path, a lone surrogate in an argument, a bug): the call ends as
            // an error the agent reads, and is journaled so, never left running.
            result = ToolResult.Error($"{name} failed: {exception.Message}");
        }

        // A command or a server's tool cut short by the stop has no result of its own (a file tool
        // runs to its end, so it has).
        if (stopping.IsCancellationRequested && (name == ToolCatalog.Bash || external is not null))
        {
            if (toolUseId is not null)
            {
                journal.Abandon(toolUseId);
            }

            return null;
        }

        // arm_bash saves its own as it runs; a skill's instructions are only any use whole.
        var sent = name is ToolCatalog.Bash or ToolCatalog.SkillLoad ? result : context.Outputs.Bound(toolUseId, result);
        if (toolUseId is not null)
        {
            var file = sent is { Raw: not null, Text.Length: > CallJournal.MaxText } ? context.Outputs.SaveSent(toolUseId, sent) : null;
            journal.Finish(toolUseId, name, sent, (long)_time.GetElapsedTime(started).TotalMilliseconds, file);
        }

        return sent;
    }

    private async Task<ToolResult> RunExternalAsync(ExternalTool tool, JsonObject? arguments, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ExternalTimeout, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            return ToolResult.Passed(await tool.Server.CallAsync(tool.RealName, arguments, either.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error($"Timed out after {ExternalTimeout.TotalSeconds:0} s: the MCP server was told to stop, and may have done part of the call.");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Cancelled: the MCP server was told to stop, and may have done part of the call.");
        }
        catch (ExternalServerFailure failure)
        {
            return ToolResult.Error(failure.Message);
        }
    }

    private Task<ToolResult> RunAsync(string name, ToolArguments arguments, string? toolUseId, CancellationToken cancellationToken) => name switch
    {
        ToolCatalog.Bash => BashTool.RunAsync(arguments, context, cancellationToken, toolUseId),
        // The file tools are quick and can't be cut short: off the reading thread, run to the end.
        ToolCatalog.FileRead => Task.Run(() => FileReadTool.Run(arguments, context), CancellationToken.None),
        ToolCatalog.FileEdit => Task.Run(() => FileEditTool.Run(arguments, context), CancellationToken.None),
        ToolCatalog.FileCreate => Task.Run(() => FileCreateTool.Run(arguments, context), CancellationToken.None),
        ToolCatalog.FileDelete => Task.Run(() => FileDeleteTool.Run(arguments, context), CancellationToken.None),
        ToolCatalog.FileCopy => Task.Run(() => FileTransfer.Copy(arguments, context), CancellationToken.None),
        ToolCatalog.FileMove => Task.Run(() => FileTransfer.Move(arguments, context), CancellationToken.None),
        ToolCatalog.SkillLoad => Task.Run(() => SkillLoadTool.Run(arguments, context), CancellationToken.None),
        _ => throw new InvalidOperationException($"No handler for the catalog's tool '{name}'."),
    };
}
