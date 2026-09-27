using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

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
    public async Task SupervisorLost_OrphanIsKilled_AsTheAgentUser()
    {
        var account = OwnAccount();
        var (first, provider) = Server(account);
        var launch = Launch("stub:gate");
        _ = provider.Start(launch);
        await Until(() => SupervisorFiles.ReadInfo(Folder(launch)) is not null);
        first.Dispose();
        var info = SupervisorFiles.ReadInfo(Folder(launch))!;
        Posix.Kill(info.SupervisorPid, Posix.SigKill);
        var (second, _) = Server(account);
        await Until(() => !second.IsListening(launch.SessionId));

        second.KillOrphan(info);

        await Assert.That(SudoCalls()).Contains($"-n -u {account.Name} -- {Supervisor} kill {info.SupervisorPid} {info.AgentPid}");
        await Until(() => Posix.StartTime(info.AgentPid) != info.AgentStartTime);
    }

    [Test]
    public async Task KillCommand_KillsTheProcessGroup()
    {
        // A process group of its own, like a supervisor's: a shell leading it and a child in it.
        using var leader = Process.Start(new ProcessStartInfo("setsid", ["sh", "-c", "sleep 60 & echo $!; wait"])
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        var child = int.Parse(leader.StandardOutput.ReadLine()!, System.Globalization.CultureInfo.InvariantCulture);

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
        using var kill = Process.Start(Supervisor, arguments.Split(' '));
        await kill.WaitForExitAsync().WaitAsync(Guard);

        await Assert.That(kill.ExitCode).IsEqualTo(2);
    }

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
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            using var reader = new StreamReader(new NetworkStream(socket, ownsSocket: false));
            var line = await reader.ReadLineAsync().WaitAsync(Guard);
            return line is not null && JsonSerializer.Deserialize(line, SupervisorJsonContext.Default.SupervisorMessage)?.Type == SupervisorMessage.Hello;
        }
        finally
        {
            Posix.Kill(-info.SupervisorPid, Posix.SigKill);
        }
    }

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
    private (Supervisors Supervisors, ClaudeProvider Provider) Server(AgentAccount account)
    {
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions
        {
            SocketRoot = Path.Combine(_root, "sockets"), User = account.Name, Sudo = SudoStub(),
        }), NullLogger<Supervisors>.Instance);
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
