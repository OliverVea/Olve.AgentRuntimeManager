using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Tools.Journal;
using Olve.AgentRuntimeManager.Tools.Protocol;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// Runs a <c>tools/call</c>: only the tools the session named, each id at most once (the
/// <see cref="CallJournal"/>), and a failure of any kind as a result the agent reads.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ToolDispatcher(ToolContext context, IReadOnlyCollection<string> tools, CallJournal journal)
{
    /// <summary>
    /// The call's result; null if the server's stop (<paramref name="stopping"/>) cut it short: it's
    /// then left as started in the journal, interrupted, and nothing is answered.
    /// </summary>
    public async Task<ToolResult?> CallAsync(string name, JsonObject? arguments, string? toolUseId, CancellationToken cancelled, CancellationToken stopping)
    {
        if (!tools.Contains(name))
        {
            var served = tools.Count == 0 ? "none" : string.Join(", ", ToolCatalog.All.Where(tool => tools.Contains(tool.Name)).Select(tool => tool.Name));
            return ToolResult.Error($"There's no tool '{name}' in this session. Its tools: {served}.");
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
        ToolResult result;
        try
        {
            result = await RunAsync(name, new ToolArguments(arguments), either.Token);
        }
        catch (Exception exception)
        {
            // Anything (a NUL in a path, a lone surrogate in an argument, a bug): the call ends as
            // an error the agent reads, and is journaled so, never left running.
            result = ToolResult.Error($"{name} failed: {exception.Message}");
        }

        // A command cut short by the stop has no result of its own (a file tool runs to its end, so it has).
        if (stopping.IsCancellationRequested && name == ToolCatalog.Bash)
        {
            if (toolUseId is not null)
            {
                journal.Abandon(toolUseId);
            }

            return null;
        }

        if (toolUseId is not null)
        {
            journal.Finish(toolUseId, name, result);
        }

        return result;
    }

    private Task<ToolResult> RunAsync(string name, ToolArguments arguments, CancellationToken cancellationToken) => name switch
    {
        ToolCatalog.Bash => BashTool.RunAsync(arguments, context, cancellationToken),
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
