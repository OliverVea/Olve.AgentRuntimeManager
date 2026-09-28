using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Supervision;

/// <summary>
/// Agents as their own user (docs/AGENT-USER.md). The tests have neither a second user nor sudo:
/// the "agent user" is the test's own, and a stub stands in for sudo (Stub/sudo-stub.sh), so what
/// shows is how ARM goes through sudo, not the user switch itself (that's checked on beta).
/// </summary>
[UnsupportedOSPlatform("windows")]
public class AgentUserTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly string Supervisor = Path.Combine(AppContext.BaseDirectory, "olve-arm-supervisor");

    private readonly string _root = Directory.CreateTempSubdirectory("arm-agent-user-").FullName;
    private readonly List<Supervisors> _servers = [];

    [After(Test)]
    public void Cleanup()
    {
        foreach (var server in _servers)
        {
            server.Dispose();
        }

        TestProcesses.KillSupervisors(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task Parse_PasswdLine_NameAndHome() =>
        await Assert.That(AgentAccount.Parse("arm-agent:x:1001:1001::/home/arm-agent:/bin/bash\n"))
            .IsEqualTo(new AgentAccount("arm-agent", "/home/arm-agent"));

    [Test]
    [Arguments("")]
    [Arguments("arm-agent")]
    [Arguments("arm-agent:x:1001:1001::/home/arm-agent")]
    public async Task Parse_NotAPasswdLine_IsNull(string line) =>
        await Assert.That(AgentAccount.Parse(line)).IsNull();

    [Test]
    public async Task Find_Unset_RunsAsArmsOwnUser() =>
        await Assert.That(AgentAccount.Find("")).IsNull();

    [Test]
    public async Task Find_NoSuchUser_Throws() =>
        await Assert.That(() => AgentAccount.Find("no-such-arm-agent-user")).Throws<InvalidOperationException>();

    [Test]
    public async Task Identify_ReplacesArmsIdentity_KeepsTheRest()
    {
        var environment = new AgentAccount("arm-agent", "/home/arm-agent")
            .Identify(new Dictionary<string, string> { ["HOME"] = "/home/arm", ["USER"] = "arm", ["PATH"] = "/usr/bin" });

        await Assert.That(environment).IsEquivalentTo(new Dictionary<string, string>
        {
            ["HOME"] = "/home/arm-agent", ["USER"] = "arm-agent", ["LOGNAME"] = "arm-agent", ["PATH"] = "/usr/bin",
        });
    }

    [Test]
    public async Task Agent_StartsThroughSudo_AsTheAgentUser()
    {
        var account = OwnAccount();
        var (_, provider) = Server(account);
        var launch = Launch("Say READY");

        var outcome = await provider.Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(SudoCalls()).IsEquivalentTo([$"-n -u {account.Name} -- {Supervisor}"]);
        var environment = Environment(launch);
        await Assert.That(environment["HOME"]).IsEqualTo(account.Home);
        await Assert.That(environment["USER"]).IsEqualTo(account.Name);
        await Assert.That(environment["LOGNAME"]).IsEqualTo(account.Name);
    }

    [Test]
    public async Task Agent_SessionFolderAndWorkplace_AreSharedWithTheAgentUsersGroup()
    {
        var (_, provider) = Server(OwnAccount());
        var launch = Launch("Say READY");

        await provider.Start(launch).Completion.WaitAsync(Guard);

        const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
        await Assert.That(File.GetUnixFileMode(Folder(launch)) & shared).IsEqualTo(shared);
        await Assert.That(File.GetUnixFileMode(Path.Combine(Folder(launch), "work")) & shared).IsEqualTo(shared);
    }

    [Test]
    public async Task Workplace_New_IsMadeByTheSupervisor()
    {
        // As the agent's user on the VMs: its own, so git trusts a repository right in it.
        var (_, provider) = Server(OwnAccount());
        var launch = Launch("Say READY");
        var work = Path.Combine(Folder(launch), "work");

        _ = provider.Start(launch);
        var madeBeforeTheSupervisor = Directory.Exists(work);
        await Until(() => SupervisorFiles.ReadExit(Folder(launch)) is not null);

        await Assert.That(madeBeforeTheSupervisor).IsFalse();
        await Assert.That(File.Exists(Path.Combine(work, "prompt.jsonl"))).IsTrue();
    }

    [NotInParallel("signals")]
    [Test]
    public async Task SupervisorLost_OrphanIsKilled_AsTheAgentUser()
    {
        var account = OwnAccount();
        var (first, provider) = Server(account);
        var launch = Launch("stub:gate");
        _ = provider.Start(launch);
        // Its prompt taken, or the agent ends by itself once its supervisor is gone (no orphan to kill).
        await Until(() => SupervisorFiles.ReadInfo(Folder(launch)) is not null && File.Exists(Path.Combine(Folder(launch), "work", "prompt.jsonl")));
        first.Dispose();
        var info = SupervisorFiles.ReadInfo(Folder(launch))!;
        Posix.Kill(info.SupervisorPid, Posix.SigKill);
        var (second, _) = Server(account);
        await Until(() => !second.IsListening(launch.SessionId));

        second.KillOrphan(info);

        await Assert.That(SudoCalls()).Contains($"-n -u {account.Name} -- {Supervisor} kill {info.SupervisorPid} {info.AgentPid}");
        await Until(() => Posix.StartTime(info.AgentPid) != info.AgentStartTime);
    }

    [NotInParallel("signals")]
    [Test]
    public async Task KillOrphan_WithAnAgentUser_NeverSignalsAsArm()
    {
        // The sudo that would kill as the agent user refuses: nothing may be killed directly instead.
        using var sleeper = Sleeper();
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions
        {
            SocketRoot = Path.Combine(_root, "sockets"), User = OwnAccount().Name, Sudo = "/bin/false",
        }), NullLogger<Supervisors>.Instance);
        _servers.Add(supervisors);

        var gone = supervisors.KillOrphan(Info(sleeper.Id, sleeper.Id));

        await Task.Delay(200);
        await Assert.That(sleeper.HasExited).IsFalse();
        await Assert.That(gone).IsFalse();
    }

    [NotInParallel("signals")]
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task KillOrphan_APidOfOneOrLess_KillsNothing(bool supervisorPidZero)
    {
        // An agent writes supervisor.json: 0 would be ARM's own process group, 1 (as -1) every process of ARM's user.
        using var sleeper = Sleeper();
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }),
            NullLogger<Supervisors>.Instance);
        _servers.Add(supervisors);

        var gone = supervisors.KillOrphan(supervisorPidZero ? Info(0, sleeper.Id) : Info(sleeper.Id, 1));

        await Task.Delay(200);
        await Assert.That(sleeper.HasExited).IsFalse();
        // Nothing checked, so an agent may still run.
        await Assert.That(gone).IsFalse();
    }

    [NotInParallel("signals")]
    [Test]
    public async Task SupervisorLost_OrphanCantBeKilled_FailsTheSession_InsteadOfResuming()
    {
        // As an agent from before the agent user would be: the kill as that user can't reach it.
        var account = OwnAccount();
        var (first, provider) = Server(account);
        var launch = Launch("stub:gate");
        _ = provider.Start(launch);
        await Until(() => SupervisorFiles.ReadInfo(Folder(launch)) is not null && File.Exists(Path.Combine(Folder(launch), "work", "prompt.jsonl")));
        first.Dispose();
        var info = SupervisorFiles.ReadInfo(Folder(launch))!;
        Posix.Kill(info.SupervisorPid, Posix.SigKill);
        var (second, recovering) = Server(account, sudo: "/bin/false");
        await Until(() => !second.IsListening(launch.SessionId));
        try
        {
            var recovery = new AgentRecovery(launch.SessionId, launch.Prompt, launch.Model, launch.ProviderSessionId.ToString(), launch.RunId, Guid.NewGuid());

            var recovered = recovering.Recover(recovery)!;

            await Assert.That(recovered.Resumed).IsFalse();
            await Assert.That(await recovered.Run.Completion.WaitAsync(Guard)).IsEqualTo(new AgentOutcome.Failed(ClaudeProvider.OrphanAlive));
            await Assert.That(Posix.StartTime(info.AgentPid)).IsEqualTo(info.AgentStartTime);
            await Assert.That(SupervisorFiles.ReadInfo(Folder(launch))!.RunId).IsEqualTo(launch.RunId);
        }
        finally
        {
            if (info.SupervisorPid > 1)
            {
                Posix.Kill(-info.SupervisorPid, Posix.SigKill);
            }
        }
    }

    [NotInParallel("signals")]
    [Test]
    public async Task KillCommand_AProcessItCantSignal_ExitsNonZero()
    {
        Skip.When(Posix.GetEUid() == 0, "As root it could signal anything: this would kill a real process.");
        var root = Directory.GetDirectories("/proc").Select(d => int.TryParse(Path.GetFileName(d), out var pid) ? pid : 0)
            .First(pid => pid > 1 && OwnerUid(pid) == 0);

        using var kill = Process.Start(new ProcessStartInfo(Supervisor, ["kill", root.ToString(System.Globalization.CultureInfo.InvariantCulture), root.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        kill.StandardInput.Close();
        var error = await kill.StandardError.ReadToEndAsync().WaitAsync(Guard);
        await kill.WaitForExitAsync().WaitAsync(Guard);

        await Assert.That(kill.ExitCode).IsEqualTo(1);
        await Assert.That(error).Contains("could not signal");

        static int? OwnerUid(int pid)
        {
            try
            {
                var line = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal));
                return line is null ? null : int.Parse(line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    [Test]
    public async Task SessionFiles_ThroughALink_AreNotRead()
    {
        var (_, provider) = Server(OwnAccount());
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var recorded = Path.Combine(Path.GetDirectoryName(ClaudeProviderTests.Stub)!, "success.jsonl");
        File.Copy(recorded, Path.Combine(elsewhere, SupervisorFiles.Output));
        var linked = Directory.CreateDirectory(Path.Combine(_root, "sessions", Guid.NewGuid().ToString())).FullName;
        File.CreateSymbolicLink(Path.Combine(linked, SupervisorFiles.Output), Path.Combine(elsewhere, SupervisorFiles.Output));
        SupervisorFiles.WriteAtomically(Path.Combine(elsewhere, SupervisorFiles.Info), Info(1234, 1234), SupervisorJsonContext.Default.SupervisorInfo);
        File.CreateSymbolicLink(Path.Combine(linked, SupervisorFiles.Info), Path.Combine(elsewhere, SupervisorFiles.Info));
        var real = Directory.CreateDirectory(Path.Combine(_root, "sessions", Guid.NewGuid().ToString())).FullName;
        File.Copy(recorded, Path.Combine(real, SupervisorFiles.Output));

        await Assert.That(provider.Conversation(Guid.Parse(Path.GetFileName(linked)))).IsEmpty();
        await Assert.That(SupervisorFiles.ReadInfo(linked)).IsNull();
        await Assert.That(SupervisorFiles.ReadInfo(elsewhere)).IsNotNull();
        await Assert.That(provider.Conversation(Guid.Parse(Path.GetFileName(real)))).IsNotEmpty();
    }

    [Test]
    public async Task SessionOutput_AFifo_IsNotRead_AndDoesNotBlock()
    {
        var (_, provider) = Server(OwnAccount());
        var folder = Directory.CreateDirectory(Path.Combine(_root, "sessions", Guid.NewGuid().ToString())).FullName;
        using (var mkfifo = Process.Start("mkfifo", [Path.Combine(folder, SupervisorFiles.Output)]))
        {
            await mkfifo.WaitForExitAsync().WaitAsync(Guard);
        }

        var conversation = await Task.Run(() => provider.Conversation(Guid.Parse(Path.GetFileName(folder)))).WaitAsync(Guard);

        await Assert.That(conversation).IsEmpty();
    }

    [Test]
    public async Task Workplace_ALink_IsNotShared_AndTheLaunchFails()
    {
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var account = OwnAccount();
        var log = new ListLogger<Supervisors>();
        var (_, provider) = Server(account, log: log);
        var launch = Launch("Say READY");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.SetUnixFileMode(target, privateMode);
        Directory.CreateDirectory(Folder(launch));
        var work = Path.Combine(Folder(launch), "work");
        Directory.CreateSymbolicLink(work, target);

        // Self-diagnosing (it failed once, only in the pipeline's pod, as root): every precondition
        // and the branch Supervisors took go into the failure message.
        string opened;
        using (var handle = Posix.OpenNoFollow(work, directory: true))
        {
            opened = handle is null ? "refused" : "OPENED";
        }

        var preconditions = string.Join("; ",
            $"euid {Posix.GetEUid()}", $"Environment.UserName '{System.Environment.UserName}'", $"account {account}",
            $"Supervisor:User '{account.Name}'", $"arch {RuntimeInformation.ProcessArchitecture}/{RuntimeInformation.OSArchitecture}",
            $"os {RuntimeInformation.OSDescription}", $"work links to '{new FileInfo(work).LinkTarget}'",
            $"Directory.Exists(work) {Directory.Exists(work)}", $"ExistsNoFollow(work) {Posix.ExistsNoFollow(work)}",
            $"OpenNoFollow(work, directory) {opened}");
        Console.WriteLine($"Workplace_ALink preconditions: {preconditions}");
        await Assert.That(new FileInfo(work).LinkTarget).IsEqualTo(target).Because(preconditions);
        await Assert.That(Directory.Exists(work)).IsTrue().Because(preconditions);
        await Assert.That(Posix.ExistsNoFollow(work)).IsTrue().Because(preconditions);
        await Assert.That(opened).IsEqualTo("refused").Because(preconditions);

        IAgentRun? run = null;
        Exception? thrown = null;
        try
        {
            run = provider.Start(launch);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        try
        {
            var said = $"{preconditions}; Supervisors said: {string.Join(" | ", log.Messages)}";
            Console.WriteLine($"Workplace_ALink: {(thrown is null ? "no exception" : $"{thrown.GetType().Name}: {thrown.Message}")}; {said}");
            await Assert.That(thrown).IsTypeOf<InvalidOperationException>().Because(said);
            await Assert.That(File.GetUnixFileMode(target)).IsEqualTo(privateMode).Because(said);
        }
        finally
        {
            // A launch that should have failed and didn't started a detached supervisor: end it here.
            if (run is not null)
            {
                run.Kill();
                await run.Completion.WaitAsync(Guard);
            }
        }
    }

    [Test]
    public async Task StartTime_AZombie_IsNotRunning()
    {
        // A child that has ended under a parent that never reaps it (as an orphan is under a pod's pid 1
        // that doesn't reap): a zombie, whose /proc entry stays.
        using var parent = Process.Start(new ProcessStartInfo("sh", ["-c", "sleep 0 & echo $!; exec sleep 30"])
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        try
        {
            var zombie = int.Parse((await parent.StandardOutput.ReadLineAsync().WaitAsync(Guard))!, System.Globalization.CultureInfo.InvariantCulture);
            await Until(() => File.ReadAllText($"/proc/{zombie}/stat").Split(") ")[1].StartsWith('Z'));

            await Assert.That(Posix.StartTime(zombie)).IsNull();
            await Assert.That(Posix.StartTime(parent.Id)).IsNotNull();
        }
        finally
        {
            parent.Kill();
            await parent.WaitForExitAsync().WaitAsync(Guard);
        }
    }

    [NotInParallel("signals")]
    [Test]
    public async Task KillCommand_KillsTheProcessGroup()
    {
        // A process group of its own, like a supervisor's: a shell leading it and a child in it.
        using var leader = Process.Start(new ProcessStartInfo("setsid", ["sh", "-c", "sleep 60 & echo $!; wait"])
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        var child = int.Parse((await leader.StandardOutput.ReadLineAsync().WaitAsync(Guard))!, System.Globalization.CultureInfo.InvariantCulture);

        using var kill = Process.Start(Supervisor, ["kill", leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), child.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        await kill.WaitForExitAsync().WaitAsync(Guard);

        await Assert.That(kill.ExitCode).IsEqualTo(0);
        await leader.WaitForExitAsync().WaitAsync(Guard);
        await Until(() => Posix.StartTime(child) is null);
    }

    [Test]
    [Arguments("kill")]
    [Arguments("kill 1 2")]
    [Arguments("kill -5 x")]
    public async Task KillCommand_WithoutAProperGroupAndPid_Refuses(string arguments)
    {
        // Stdin closed: were it taken for a launch instead, that would show as exit code 2 too, not hang.
        using var kill = Process.Start(new ProcessStartInfo(Supervisor, arguments.Split(' ')) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false })!;
        kill.StandardInput.Close();
        var error = await kill.StandardError.ReadToEndAsync().WaitAsync(Guard);
        await kill.WaitForExitAsync().WaitAsync(Guard);

        await Assert.That(kill.ExitCode).IsEqualTo(2);
        await Assert.That(error).Contains("kill <process group> <pid>");
    }

    [NotInParallel("signals")]
    [Test]
    public async Task Socket_OnlyTheClientUserMayConnect()
    {
        var allowed = await HelloAsync(Posix.GetEUid());
        var refused = await HelloAsync(Posix.GetEUid() + 1);

        await Assert.That(allowed).IsTrue();
        await Assert.That(refused).IsFalse();
    }

    /// <summary>Whether a supervisor whose launch allows <paramref name="clientUid"/> says hello to this process.</summary>
    private async Task<bool> HelloAsync(int clientUid)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString())).FullName;
        var socketPath = Path.Combine(_root, "sockets", $"{Guid.NewGuid()}.sock");
        var launch = new SupervisorLaunch
        {
            Version = SupervisorProtocol.Version, RunId = Guid.NewGuid(), Command = "/bin/sleep", Arguments = ["60"],
            Environment = new Dictionary<string, string>(), WorkingDirectory = folder, Folder = folder,
            SocketPath = socketPath, ClientUid = clientUid,
        };
        using (var supervisor = Process.Start(new ProcessStartInfo(Supervisor) { RedirectStandardInput = true, UseShellExecute = false })!)
        {
            await supervisor.StandardInput.WriteAsync(JsonSerializer.Serialize(launch, SupervisorJsonContext.Default.SupervisorLaunch));
            supervisor.StandardInput.Close();
        }

        await Until(() => SupervisorFiles.ReadInfo(folder) is not null && File.Exists(socketPath));
        var info = SupervisorFiles.ReadInfo(folder)!;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).WaitAsync(Guard);
            using var reader = new StreamReader(new NetworkStream(socket, ownsSocket: false));
            var line = await reader.ReadLineAsync().WaitAsync(Guard);
            return line is not null && JsonSerializer.Deserialize(line, SupervisorJsonContext.Default.SupervisorMessage)?.Type == SupervisorMessage.Hello;
        }
        finally
        {
            if (info.SupervisorPid > 1)
            {
                Posix.Kill(-info.SupervisorPid, Posix.SigKill);
            }
        }
    }

    /// <summary>A process leading a process group of its own (as a supervisor does), until the test ends.</summary>
    private static Sleeper Sleeper() => new(Process.Start(new ProcessStartInfo("setsid", ["sleep", "60"]) { UseShellExecute = false })!);

    private static SupervisorInfo Info(int supervisorPid, int agentPid) => new()
    {
        Version = SupervisorProtocol.Version, RunId = Guid.NewGuid(), SupervisorPid = supervisorPid, AgentPid = agentPid,
        AgentStartTime = Posix.StartTime(agentPid), OutputStart = 0, StderrStart = 0, StartedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>The test's own user, standing in for the agent user.</summary>
    private static AgentAccount OwnAccount()
    {
        try
        {
            return AgentAccount.Find(System.Environment.UserName)!;
        }
        catch (InvalidOperationException)
        {
            Skip.Test("The test's user isn't in the user database.");
            throw;
        }
    }

    /// <summary>A server whose agents run as <paramref name="account"/>, through the sudo stub.</summary>
    private (Supervisors Supervisors, ClaudeProvider Provider) Server(AgentAccount account, string? sudo = null, ILogger<Supervisors>? log = null)
    {
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions
        {
            SocketRoot = Path.Combine(_root, "sockets"), User = account.Name, Sudo = sudo ?? SudoStub(),
        }), log ?? NullLogger<Supervisors>.Instance);
        _servers.Add(supervisors);
        var options = new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = Path.Combine(_root, "sessions") };
        return (supervisors, new ClaudeProvider(Options.Create(options), supervisors, NullLogger<ClaudeProvider>.Instance));
    }

    /// <summary>A copy of the sudo stub in the test's folder, so its log (<c>sudo.log</c>) is this test's.</summary>
    private string SudoStub()
    {
        var path = Path.Combine(_root, "sudo");
        if (!File.Exists(path))
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Sessions", "Supervision", "Stub", "sudo-stub.sh"), path);
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }

        return path;
    }

    private string[] SudoCalls() => File.ReadAllLines(Path.Combine(_root, "sudo.log"));

    private static AgentLaunch Launch(string prompt)
    {
        var id = Guid.NewGuid();
        return new(id, prompt, "sonnet", 1, id, Guid.NewGuid());
    }

    private string Folder(AgentLaunch launch) => Path.Combine(_root, "sessions", launch.SessionId.ToString());

    /// <summary>The environment the stub CLI was started with.</summary>
    private Dictionary<string, string> Environment(AgentLaunch launch) =>
        File.ReadAllLines(Path.Combine(Folder(launch), "work", "invocation.txt"))
            .Where(l => l.StartsWith("env:", StringComparison.Ordinal))
            .Select(l => l[4..].Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Guard);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

/// <summary>A sleeping process, killed when disposed.</summary>
internal sealed class Sleeper(Process process) : IDisposable
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.Kill();
        }

        process.Dispose();
    }
}
