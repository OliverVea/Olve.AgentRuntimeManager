using System.Text.Json;
using Olve.AgentRuntimeManager.Supervisor;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

// olve-arm-supervisor: reads a SupervisorLaunch (one JSON document) from stdin, detaches from ARM,
// runs the agent and serves the session's socket until the agent has ended.
// Unix only: sockets and file modes, setsid, process groups (ARM runs on Linux).
if (OperatingSystem.IsWindows())
{
    await Console.Error.WriteLineAsync("olve-arm-supervisor: Unix only.");
    return 2;
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
