using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary>
/// The built <c>olve-arm-tools</c> driven as an agent drives it: MCP over its real stdin and
/// stdout, one JSON-RPC message per line, as ARM launches it (<see cref="ToolsLaunch"/>).
/// </summary>
[UnsupportedOSPlatform("windows")]
public class ToolsServerTests
{
    private static readonly string Server = Path.Combine(AppContext.BaseDirectory, "olve-arm-tools");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly ToolsTestFolder _folder = new();
    private readonly List<Process> _processes = [];

    [After(Test)]
    public void Cleanup()
    {
        foreach (var process in _processes)
        {
            // This host's own child, still running only if the test failed.
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }

            process.Dispose();
        }

        _folder.Dispose();
    }

    private Process Start(params string[] tools)
    {
        var info = new ProcessStartInfo(Server)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])[ToolsLaunch.Workplace, _folder.Workplace, ToolsLaunch.Journal, _folder.PathOf(ToolsLaunch.JournalFileName)])
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var tool in tools)
        {
            info.ArgumentList.Add(ToolsLaunch.Tool);
            info.ArgumentList.Add(tool);
        }

        var process = Process.Start(info)!;
        _processes.Add(process);
        process.ErrorDataReceived += (_, line) => Console.WriteLine($"[olve-arm-tools] {line.Data}");
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task Send(Process process, string message)
    {
        await process.StandardInput.WriteLineAsync(message);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonObject> Receive(Process process)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The server's output ended.");
        return (JsonObject)JsonNode.Parse(line)!;
    }

    private static async Task Initialize(Process process)
    {
        await Send(process, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        await Receive(process);
        await Send(process, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    }

    private static string CallMessage(int id, string tool, string arguments, string? toolUseId = null)
    {
        var parameters = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments) };
        if (toolUseId is not null)
        {
            parameters["_meta"] = new JsonObject { ["claudecode/toolUseId"] = toolUseId, ["progressToken"] = id };
        }

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = parameters }.ToJsonString();
    }

    [Test]
    public async Task Server_Initializes_ListsTheNamedTools_AndRunsACall()
    {
        var process = Start(ToolCatalog.Bash, ToolCatalog.FileRead);

        // Claude Code asks this first, and initializes when it isn't known.
        await Send(process, """{"jsonrpc":"2.0","id":"server-discover-probe-1","method":"server/discover","params":{}}""");
        var discover = await Receive(process);
        await Send(process, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        var initialized = await Receive(process);
        await Send(process, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await Send(process, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var list = await Receive(process);
        await Send(process, CallMessage(2, ToolCatalog.Bash, """{"command":"echo hello"}""", "toolu_1"));
        var call = await Receive(process);

        await Assert.That(discover["id"]!.GetValue<string>()).IsEqualTo("server-discover-probe-1");
        await Assert.That(discover["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32601);
        await Assert.That(initialized["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-06-18");
        await Assert.That(initialized["result"]!["serverInfo"]!["name"]!.GetValue<string>()).IsEqualTo(ToolsLaunch.ServerName);
        var tools = list["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        await Assert.That(tools).IsEquivalentTo((string[])[ToolCatalog.Bash, ToolCatalog.FileRead]);
        await Assert.That(list["result"]!["tools"]![0]!["inputSchema"]!["required"]![0]!.GetValue<string>()).IsEqualTo("command");
        await Assert.That(call["id"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(call["result"]!["isError"]!.GetValue<bool>()).IsFalse();
        await Assert.That(call["result"]!["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("hello\n\nExit code: 0");
    }

    [Test]
    public async Task Server_AnUnknownProtocolVersion_GetsItsNewest()
    {
        var process = Start();

        await Send(process, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        var initialized = await Receive(process);

        await Assert.That(initialized["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-11-25");
    }

    [Test]
    public async Task Server_AnswersWhileACallRuns_AndCancelsIt()
    {
        var process = Start(ToolCatalog.Bash);
        await Initialize(process);
        var marker = ToolsTestFolder.Marker();

        await Send(process, CallMessage(1, ToolCatalog.Bash, $$"""{"command":"exec -a {{marker}} sleep 300"}"""));
        await Send(process, """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        var ping = await Receive(process);
        await WaitFor(() => TestProcesses().Count > 0, marker);
        await Send(process, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"reason":"test"}}""");
        await Send(process, """{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        var next = await Receive(process);

        await Assert.That(ping["id"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(await ToolsTestFolder.Gone(marker)).IsTrue();
        // The cancelled call is never answered: the next message is the ping's.
        await Assert.That(next["id"]!.GetValue<int>()).IsEqualTo(3);

        List<int> TestProcesses() => Support.TestProcesses.WithCommandLine(marker);
    }

    [Test]
    [Arguments("SIGTERM")]
    [Arguments("end of input")]
    public async Task Server_Stopping_StopsItsCalls_WhichThenReadAsInterrupted(string how)
    {
        var process = Start(ToolCatalog.Bash);
        await Initialize(process);
        var marker = ToolsTestFolder.Marker();
        await Send(process, CallMessage(1, ToolCatalog.Bash, $$"""{"command":"(exec -a {{marker}} sleep 300) & wait"}""", "toolu_1"));
        await WaitFor(() => Support.TestProcesses.WithCommandLine(marker).Count > 0, marker);

        if (how == "SIGTERM")
        {
            // Only while it's still this host's running child (TestProcesses' rule: a pid alone proves nothing).
            await Assert.That(Posix.Status(process.Id) is { IsProcess: true, IsRunning: true } status && status.Parent == Environment.ProcessId).IsTrue();
            Posix.Kill(process.Id, Posix.SigTerm);
        }
        else
        {
            process.StandardInput.Close();
        }

        using (var timeout = new CancellationTokenSource(Wait))
        {
            await process.WaitForExitAsync(timeout.Token);
        }

        await Assert.That(process.ExitCode).IsEqualTo(0);
        await Assert.That(await ToolsTestFolder.Gone(marker)).IsTrue();

        var restarted = Start(ToolCatalog.Bash);
        await Initialize(restarted);
        await Send(restarted, CallMessage(1, ToolCatalog.Bash, """{"command":"echo again"}""", "toolu_1"));
        var again = await Receive(restarted);
        await Assert.That(again["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(again["result"]!["content"]![0]!["text"]!.GetValue<string>()).Contains("was interrupted");
    }

    [Test]
    public async Task Server_ABadLaunch_ExitsWithAnError()
    {
        using var process = Process.Start(new ProcessStartInfo(Server, ["--tool", "arm_nope"]) { RedirectStandardError = true, UseShellExecute = false })!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(process.ExitCode).IsEqualTo(2);
        await Assert.That(error).Contains("unknown tool 'arm_nope'");
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited in vain for {what}.");
            }

            await Task.Delay(25);
        }
    }
}
