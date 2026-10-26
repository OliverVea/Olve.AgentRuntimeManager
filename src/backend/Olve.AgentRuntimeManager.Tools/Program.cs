using System.Runtime.InteropServices;
using Olve.AgentRuntimeManager.Tools;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.Journal;
using Olve.AgentRuntimeManager.Tools.Server;
using Olve.AgentRuntimeManager.Tools.Tools;

// `olve-arm-tools --workplace <folder> --journal <file> --tool <name>…`: ARM's tools (M10) for one
// session, served to the agent as a stdio MCP server (docs/MILESTONES.md M10). It ends when its
// input does, on SIGTERM (Claude Code ends its MCP servers with a signal, even on a normal exit)
// and when the agent dies (ParentDeath), stopping every running call with it.
// Unix only: process groups and signals (ARM runs on Linux).
if (OperatingSystem.IsWindows())
{
    await Console.Error.WriteLineAsync("olve-arm-tools: Unix only.");
    return 2;
}

if (ToolsOptions.Parse(args, out var error) is not { } options)
{
    await Console.Error.WriteLineAsync($"olve-arm-tools: {error}");
    return 2;
}

CallJournal journal;
try
{
    journal = new CallJournal(options.Journal);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync($"olve-arm-tools: can't open the journal {options.Journal}: {exception.Message}");
    return 2;
}

using (journal)
{
    var groups = new ProcessGroups();
    var dispatcher = new ToolDispatcher(new ToolContext(options.Workplace, groups), options.Tools, journal);
    var server = new McpServer(options, dispatcher, groups, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);

    var stop = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
    using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
    if (ParentDeath.Watch() is false)
    {
        stop.TrySetResult("the agent that started it is gone");
    }

    await Console.Error.WriteLineAsync($"olve-arm-tools: serving {string.Join(", ", options.Tools)} for {options.Workplace}.");
    var reading = server.ReadAsync();
    var ended = await Task.WhenAny(reading, stop.Task);
    var reason = ended == reading
        ? reading.IsFaulted ? $"input failed: {reading.Exception?.InnerException?.Message}" : "end of input"
        : await stop.Task;
    await server.StopAsync(reason);
    return 0;

    void Stop(PosixSignalContext context)
    {
        context.Cancel = true;
        stop.TrySetResult(context.Signal.ToString());
    }
}
