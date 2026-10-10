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

    // Only what is proven to be the run's: the agent a process (not some process's thread) in the
    // group, so the group's id can't name another one. Not so: the agent is gone (its pid names
    // something else now), nothing is signalled.
    if (!Posix.IsInGroup(agentPid, groupId, startTime: null))
    {
        return 0;
    }

    // One signal, to the group (the agent is in it): a second one to the agent's pid could, once the
    // agent is reaped, reach a process that took the pid since. Gone already is fine; anything else
    // (EPERM: a process of another user, e.g. an agent from before the agent user) means it may
    // still run, and ARM must not start a second one.
    if (!Posix.SignalOrGone(-groupId, Posix.SigKill, out var groupError))
    {
        await Console.Error.WriteLineAsync($"olve-arm-supervisor: could not signal process group {groupId} of pid {agentPid} (errno {groupError}).");
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
