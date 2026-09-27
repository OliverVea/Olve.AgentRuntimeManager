using System.Globalization;
using System.Text.Json;
using Olve.AgentRuntimeManager.Supervisor;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

// olve-arm-supervisor: reads a SupervisorLaunch (one JSON document) from stdin, detaches from ARM,
// runs the agent and serves the session's socket until the agent has ended.
// `olve-arm-supervisor kill <process group> <pid>`: SIGKILLs an orphaned run's process group and
// agent, for an ARM whose agents run as another user (docs/AGENT-USER.md): ARM runs this as that
// user, so it can signal exactly what that user can.
// Unix only: sockets and file modes, setsid, process groups (ARM runs on Linux).
if (OperatingSystem.IsWindows())
{
    await Console.Error.WriteLineAsync("olve-arm-supervisor: Unix only.");
    return 2;
}

if (args is ["kill", var group, var agent])
{
    if (!int.TryParse(group, CultureInfo.InvariantCulture, out var groupId) || groupId <= 1
        || !int.TryParse(agent, CultureInfo.InvariantCulture, out var agentPid) || agentPid <= 1)
    {
        await Console.Error.WriteLineAsync("olve-arm-supervisor: kill <process group> <pid>, both above 1.");
        return 2;
    }

    Posix.Kill(-groupId, Posix.SigKill);
    Posix.Kill(agentPid, Posix.SigKill);
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
