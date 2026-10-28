using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Olve.AgentRuntimeManager.Supervisor;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

// olve-arm-supervisor: reads a SupervisorLaunch (one JSON document) from stdin, detaches from ARM,
// runs the agent and serves the session's socket until the agent has ended.
// `olve-arm-supervisor kill <process group> <pid>`: SIGKILLs an orphaned run's process group and
// agent, for an ARM whose agents run as another user (docs/AGENT-USER.md): ARM runs this as that
// user, so it can signal exactly what that user can. Exit code 1: something there that it couldn't signal.
// `olve-arm-supervisor remove-worktree <shared copy> <worktree>`: removes a deleted session's
// worktree (M5e) from the shared copy of its repository, its branch kept, as the user who made it
// (ARM runs this as the agent user, like `kill`). Exit code 1: it couldn't.
// `olve-arm-supervisor skill-install`: reads a SupervisorSkillInstall (one JSON document) from stdin
// and puts the skill (M10b) in the registry, fetched from git, whole or not at all; the installed
// SKILL.md's text goes to stdout (ARM runs this as the agent user too). Exit codes: 1 the fetch
// failed (git's error on stderr), 2 a request it can't act on, 3 no valid SKILL.md at the path.
// `olve-arm-supervisor skill-remove <root> <name>`: removes the skill `<root>/<name>` from the
// registry (fine if it's gone already). Exit code 1: it couldn't.
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

if (args is ["remove-worktree", ..])
{
    if (args is not [_, var shared, var worktree] || !Path.IsPathFullyQualified(shared) || !Path.IsPathFullyQualified(worktree))
    {
        await Console.Error.WriteLineAsync("olve-arm-supervisor: remove-worktree <shared copy> <worktree>, both absolute paths.");
        return 2;
    }

    var error = await RepoWorktree.RemoveAsync(shared, worktree);
    if (error is not null)
    {
        await Console.Error.WriteLineAsync($"olve-arm-supervisor: {error}");
        return 1;
    }

    return 0;
}

if (args is ["skill-install", ..])
{
    if (args is not ["skill-install"])
    {
        await Console.Error.WriteLineAsync("olve-arm-supervisor: skill-install takes no arguments: a SupervisorSkillInstall on stdin.");
        return SkillInstaller.BadUsage;
    }

    SupervisorSkillInstall? install;
    try
    {
        install = JsonSerializer.Deserialize(await Console.In.ReadToEndAsync(), SupervisorJsonContext.Default.SupervisorSkillInstall);
    }
    catch (JsonException exception)
    {
        await Console.Error.WriteLineAsync($"olve-arm-supervisor: unreadable skill install on stdin: {exception.Message}");
        return SkillInstaller.BadUsage;
    }

    if (install is null)
    {
        await Console.Error.WriteLineAsync("olve-arm-supervisor: expected a skill install on stdin.");
        return SkillInstaller.BadUsage;
    }

    // The errors stand alone (no prefix): ARM shows them as they are, as the request's own message.
    var installed = await SkillInstaller.InstallAsync(install);
    if (installed.Error is not null)
    {
        await Console.Error.WriteLineAsync(installed.Error);
    }

    if (installed.Output is not null)
    {
        await Console.Out.WriteAsync(installed.Output);
        await Console.Out.FlushAsync();
    }

    return installed.ExitCode;
}

if (args is ["skill-remove", ..])
{
    if (args is not [_, var root, var name] || !Path.IsPathFullyQualified(root) || name.Length == 0 || name is "." or ".." || name.Contains('/'))
    {
        await Console.Error.WriteLineAsync("olve-arm-supervisor: skill-remove <root> <name>, the root an absolute path and the name a folder name.");
        return 2;
    }

    var error = SkillInstaller.Remove(root, name);
    if (error is not null)
    {
        await Console.Error.WriteLineAsync(error);
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

// A session of its own leaves the agent's process group orphaned: under gVisor (the pipeline's
// gate), stopping a process in it, as killing the agent's tree does, sends the group SIGHUP, which
// would end the supervisor before the kill is done and the exit written. A handler, not SIG_IGN:
// an ignored signal would be the agent's too, through exec.
using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
Detach.FromParent();
return await new AgentSupervisor(launch).RunAsync();
