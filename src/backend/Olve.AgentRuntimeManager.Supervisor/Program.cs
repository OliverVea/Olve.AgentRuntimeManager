using System.Globalization;
using System.Text.Json;
using Olve.AgentRuntimeManager.Supervisor;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

// olve-arm-supervisor: reads a SupervisorLaunch (one JSON document) from stdin, detaches from ARM,
// runs the agent and serves the session's socket until the agent has ended.
// `olve-arm-supervisor kill <process group> <pid>`: SIGKILLs an orphaned run's process group and
// agent, for an ARM whose agents run as another user (docs/AGENT-USER.md): ARM runs this as that
// user, so it can signal exactly what that user can. Exit code 1: something there that it couldn't signal.
// Unix only: sockets and file modes, setsid, process groups (ARM runs on Linux).
if (OperatingSystem.IsWindows())
{
    await Console.Error.WriteLineAsync("olve-arm-supervisor: Unix only.");
    return 2;
}

if (args is ["kill", ..])
{
    if (args is not [_, var group, var agent]
        || !int.TryParse(group, CultureInfo.InvariantCulture, out var groupId) || groupId <= 1
        || !int.TryParse(agent, CultureInfo.InvariantCulture, out var agentPid) || agentPid <= 1)
    {
        await Console.Error.WriteLineAsync("olve-arm-supervisor: kill <process group> <pid>, both above 1.");
        return 2;
    }

    // Gone already is fine; anything else (EPERM: a process of another user, e.g. an agent from
    // before the agent user) means it may still run, and ARM must not start a second one.
    var groupGone = Posix.SignalOrGone(-groupId, Posix.SigKill, out var groupError);
    var agentGone = Posix.SignalOrGone(agentPid, Posix.SigKill, out var agentError);
    if (!groupGone || !agentGone)
    {
        await Console.Error.WriteLineAsync($"olve-arm-supervisor: could not signal process group {groupId} (errno {groupError}) or pid {agentPid} (errno {agentError}).");
        return 1;
    }

    return 0;
}

SupervisorLaunch? launch;
try
{
    launch = JsonSerializer.Deserialize(await Console.In.ReadToEndAsync(), SupervisorJsonContext.Default.SupervisorLaunch);
}
catch (JsonException exception)
{
    await Console.Error.WriteLineAsync($"olve-arm-supervisor: unreadable launch on stdin: {exception.Message}");
    return 2;
}

if (launch is null || launch.Version != SupervisorProtocol.Version)
{
    await Console.Error.WriteLineAsync($"olve-arm-supervisor: expected a protocol {SupervisorProtocol.Version} launch on stdin.");
    return 2;
}

Detach.FromParent();
return await new AgentSupervisor(launch).RunAsync();
