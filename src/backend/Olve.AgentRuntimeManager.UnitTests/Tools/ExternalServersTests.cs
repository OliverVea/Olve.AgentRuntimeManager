using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.External;
using Olve.AgentRuntimeManager.Tools.Journal;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary>A session's external MCP servers (M10c): starting them, what fails that, and their tools' calls.</summary>
[UnsupportedOSPlatform("windows")]
public class ExternalServersTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(20);

    private readonly ToolsTestFolder _folder = new();
    private readonly List<ExternalServers> _servers = [];
    private readonly List<CallJournal> _journals = [];

    [After(Test)]
    public async Task Cleanup()
    {
        foreach (var servers in _servers)
        {
            await servers.StopAsync();
        }

        _journals.ForEach(journal => journal.Dispose());
        _folder.Dispose();
    }

    private ExternalServers Servers(params StubMcpServer[] stubs)
    {
        var file = _folder.PathOf(SupervisorFiles.McpServers);
        IReadOnlyList<McpServerDefinition> definitions = [.. stubs.Select(stub => stub.Definition())];
        SupervisorFiles.WriteAtomically(file, definitions, SupervisorJsonContext.Default.IReadOnlyListMcpServerDefinition);
        var servers = new ExternalServers(file, _folder.Workplace, _folder.Groups, TextWriter.Null);
        _servers.Add(servers);
        return servers;
    }

    private ToolDispatcher Dispatcher(ExternalServers servers, params string[] tools)
    {
        var journal = new CallJournal(_folder.PathOf("session/tools.jsonl"));
        _journals.Add(journal);
        return new ToolDispatcher(_folder.Context, tools, journal, servers);
    }

    private static Task<ToolResult?> Call(ToolDispatcher dispatcher, string name, string? id = null, string arguments = "{}", CancellationToken cancelled = default, CancellationToken stopping = default) =>
        dispatcher.CallAsync(name, JsonNode.Parse(arguments) as JsonObject, id, cancelled, stopping);

    [Test]
    public async Task Tools_ComeUnderTheServersName_MadeSafe_WithTheirOwnDefinitions()
    {
        var servers = Servers(new StubMcpServer(_folder));

        var failure = await servers.StartAsync(Plenty);

        await Assert.That(failure).IsNull();
        await Assert.That(servers.IsReady).IsTrue();
        await Assert.That(servers.Tools.Select(tool => tool.Name)).IsEquivalentTo((string[])["stub__create_entities", "stub__dotted_name"]);
        await Assert.That(servers.Tools[1].RealName).IsEqualTo("dotted.name");
        await Assert.That(servers.Tools[0].Definition["title"]!.GetValue<string>()).IsEqualTo("Create Entities");
        await Assert.That(servers.Tools[0].Definition["inputSchema"]!["required"]![0]!.GetValue<string>()).IsEqualTo("entities");
        await Assert.That(servers.Tools[0].Definition.ContainsKey("outputSchema")).IsFalse();
    }

    [Test]
    public async Task TheHandshake_IsInitializeThenInitializedThenTheList_FollowingItsPages()
    {
        var stub = new StubMcpServer(_folder)
        {
            Tools = """
                if [[ $line == *'"cursor":"two"'* ]]; then
                  printf '%s\n' '{"jsonrpc":"2.0","id":'$id',"result":{"tools":[{"name":"second","inputSchema":{"type":"object"}}]}}'
                else
                  printf '%s\n' '{"jsonrpc":"2.0","id":'$id',"result":{"tools":[{"name":"first","inputSchema":{"type":"object"}}],"nextCursor":"two"}}'
                fi
                """,
        };
        var servers = Servers(stub);

        await servers.StartAsync(Plenty);

        var methods = stub.ReceivedLines().Select(line => JsonNode.Parse(line)!["method"]!.GetValue<string>());
        await Assert.That(methods).IsEquivalentTo((string[])["initialize", "notifications/initialized", "tools/list", "tools/list"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(servers.Tools.Select(tool => tool.Name)).IsEquivalentTo((string[])["stub__first", "stub__second"]);
    }

    [Test]
    public async Task TheServersEnvironment_IsInheritedWithItsOwnOnTop_InTheWorkplace()
    {
        var dump = _folder.PathOf("env.txt");
        var stub = new StubMcpServer(_folder)
        {
            Before = $$"""printf '%s|%s|%s\n' "$STUB_OWN" "$PATH" "$PWD" > '{{dump}}'""",
            Env = new Dictionary<string, string> { ["STUB_OWN"] = "mine" },
        };
        var servers = Servers(stub);

        await servers.StartAsync(Plenty);

        await Assert.That(File.ReadAllText(dump).Trim()).IsEqualTo($"mine|{Environment.GetEnvironmentVariable("PATH")}|{_folder.Workplace}");
    }

    [Test]
    [Arguments("echo 'bad token' >&2; exit 3", "MCP server 'stub' did not start: it exited with code 3\nbad token")]
    [Arguments("exit 0", "MCP server 'stub' did not start: it exited with code 0")]
    public async Task AServerThatEnds_FailsTheStart_WithWhatItSaid(string before, string expected)
    {
        var servers = Servers(new StubMcpServer(_folder) { Before = before });

        var failure = await servers.StartAsync(Plenty);

        await Assert.That(failure).IsEqualTo(expected);
        await Assert.That(await servers.Ready).IsFalse();
    }

    [Test]
    public async Task ACommandThatIsntThere_FailsTheStart_NamingIt()
    {
        var file = _folder.PathOf(SupervisorFiles.McpServers);
        IReadOnlyList<McpServerDefinition> definitions = [new() { Name = "gone", Command = "no-such-command-here", Args = [], Env = new Dictionary<string, string>() }];
        SupervisorFiles.WriteAtomically(file, definitions, SupervisorJsonContext.Default.IReadOnlyListMcpServerDefinition);
        var servers = new ExternalServers(file, _folder.Workplace, _folder.Groups, TextWriter.Null);
        _servers.Add(servers);

        var failure = await servers.StartAsync(Plenty);

        await Assert.That(failure).StartsWith("MCP server 'gone' did not start: it exited with code ");
        await Assert.That(failure).Contains("no-such-command-here");
    }

    [Test]
    public async Task ADefinitionsFileThatCantBeRead_FailsTheStart()
    {
        var file = _folder.Write(SupervisorFiles.McpServers, "not json");
        var servers = new ExternalServers(file, _folder.Workplace, _folder.Groups, TextWriter.Null);

        var failure = await servers.StartAsync(Plenty);

        await Assert.That(failure).IsEqualTo($"The session's MCP servers can't be read from {file}.");
    }

    [Test]
    public async Task AServerThatStaysSilent_FailsTheStart_OnTheBound()
    {
        var servers = Servers(new StubMcpServer(_folder) { Initialize = ":" });

        var failure = await servers.StartAsync(TimeSpan.FromMilliseconds(300));

        await Assert.That(failure).IsEqualTo("MCP server 'stub' gave no answer in 0 s");
    }

    [Test]
    public async Task OneServerFailing_FailsTheStart_WithoutWaitingForTheOthers()
    {
        var silent = new StubMcpServer(_folder, "silent") { Initialize = ":" };
        var failing = new StubMcpServer(_folder, "failing") { Before = "exit 1" };
        var servers = Servers(silent, failing);

        var failure = await servers.StartAsync(Plenty).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(failure).IsEqualTo("MCP server 'failing' did not start: it exited with code 1");
    }

    [Test]
    [Arguments("""{"jsonrpc":"2.0","id":'$id',"error":{"code":-32603,"message":"no token"}}""", "MCP server 'stub' did not start: no token")]
    [Arguments("""{"jsonrpc":"2.0","id":'$id',"result":{"tools":[{"description":"no name"}]}}""", "MCP server 'stub' did not start: it listed a tool without a name.")]
    [Arguments("""{"jsonrpc":"2.0","id":'$id',"result":{"tools":[{"name":"a.b"},{"name":"a/b"}]}}""", "MCP server 'stub' did not start: its tools 'a.b' and 'a/b' would both be called stub__a_b.")]
    public async Task AListThatCantBeServed_FailsTheStart(string answer, string expected)
    {
        var servers = Servers(new StubMcpServer(_folder) { Tools = $"{StubMcpServer.Answer}'{answer}'" });

        await Assert.That(await servers.StartAsync(Plenty)).IsEqualTo(expected);
    }

    [Test]
    public async Task AToolNamePastTheProvidersLimit_FailsTheStart_ButOneAtItPasses()
    {
        var limit = ExternalToolNames.MaxLength - "stub__".Length;
        var fits = new string('a', limit);
        var past = new string('b', limit + 1);

        var passing = await Servers(new StubMcpServer(_folder) { Tools = $$$"""{{{StubMcpServer.Answer}}}'{"jsonrpc":"2.0","id":'$id',"result":{"tools":[{"name":"{{{fits}}}"}]}}'""" }).StartAsync(Plenty);
        var failing = await Servers(new StubMcpServer(_folder) { Tools = $$$"""{{{StubMcpServer.Answer}}}'{"jsonrpc":"2.0","id":'$id',"result":{"tools":[{"name":"{{{past}}}"}]}}'""" }).StartAsync(Plenty);

        await Assert.That(passing).IsNull();
        await Assert.That(failing).IsEqualTo(
            $"MCP server 'stub' did not start: its tool '{past}' has too long a name: stub__{past} is 119 characters, and the limit is 118.");
    }

    [Test]
    public async Task OutputThatIsntAMessage_IsSkipped_AndAnsweringItsPing_ButNotWhatItAsks()
    {
        var stub = new StubMcpServer(_folder)
        {
            Initialize = $$"""
                echo 'memory server ready'
                {{StubMcpServer.Answer}}'{"jsonrpc":"2.0","id":900,"method":"ping"}'
                {{StubMcpServer.Answer}}'{"jsonrpc":"2.0","id":901,"method":"roots/list"}'
                {{StubMcpServer.Answer}}'{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}'
                {{StubMcpServer.InitializeAnswer}}
                """,
        };
        var servers = Servers(stub);

        await Assert.That(await servers.StartAsync(Plenty)).IsNull();

        var answers = stub.ReceivedLines().Select(line => JsonNode.Parse(line)!).Where(line => line["method"] is null).ToList();
        await Assert.That(answers.Single(a => a["id"]!.GetValue<int>() == 900)["result"]).IsNotNull();
        await Assert.That(answers.Single(a => a["id"]!.GetValue<int>() == 901)["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32601);
    }

    [Test]
    public async Task ACall_ReachesTheServerByItsRealName_AndComesBackAsItWent()
    {
        var stub = new StubMcpServer(_folder);
        var servers = Servers(stub);
        await servers.StartAsync(Plenty);

        var result = await Call(Dispatcher(servers), "stub__dotted_name", "toolu_1", """{"a":1}""");

        var sent = JsonNode.Parse(stub.ReceivedLines()[^1])!;
        await Assert.That(sent["params"]!["name"]!.GetValue<string>()).IsEqualTo("dotted.name");
        await Assert.That(sent["params"]!["arguments"]!["a"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(result!.IsError).IsFalse();
        await Assert.That(result.Raw!["content"]![1]!["data"]!.GetValue<string>()).IsEqualTo("aGk=");
        await Assert.That(result.Raw!["structuredContent"]!["made"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task AnErrorResult_IsAnErrorThatGoesThroughAsItIs()
    {
        var call = $$$"""{{{StubMcpServer.Answer}}}'{"jsonrpc":"2.0","id":'$id',"result":{"content":[{"type":"text","text":"bad token"}],"isError":true}}'""";
        var servers = Servers(new StubMcpServer(_folder) { Call = call });
        await servers.StartAsync(Plenty);

        var result = await Call(Dispatcher(servers), "stub__create_entities");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Raw!["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("bad token");
    }

    [Test]
    public async Task ARefusal_IsAResultTheAgentReads()
    {
        var call = $$$"""{{{StubMcpServer.Answer}}}'{"jsonrpc":"2.0","id":'$id',"error":{"code":-32602,"message":"Tool nope not found"}}'""";
        var servers = Servers(new StubMcpServer(_folder) { Call = call });
        await servers.StartAsync(Plenty);

        var result = await Call(Dispatcher(servers), "stub__create_entities");

        await Assert.That(result).IsEqualTo(ToolResult.Error("MCP server 'stub' refused the call: Tool nope not found"));
    }

    [Test]
    public async Task AnIdThatFinished_GetsItsWholeResultAgain_WithoutCallingTheServer()
    {
        var stub = new StubMcpServer(_folder);
        var servers = Servers(stub);
        await servers.StartAsync(Plenty);

        var first = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");
        var calls = stub.ReceivedLines().Length;
        // A new server (a resumed agent's) reads the journal anew.
        var again = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");

        await Assert.That(stub.ReceivedLines().Length).IsEqualTo(calls);
        await Assert.That(again!.Raw!.ToJsonString()).IsEqualTo(first!.Raw!.ToJsonString());
    }

    /// <summary>A server whose call answers with <paramref name="result"/>, however large (it's too long for a command line).</summary>
    private StubMcpServer Answering(string result)
    {
        var file = _folder.Write("answer.json", $$"""{"result":{{result}},"jsonrpc":"2.0","id":ID}""");
        return new StubMcpServer(_folder) { Call = $"sed \"s/ID/$id/\" '{file}'; echo" };
    }

    [Test]
    public async Task ALargeResult_IsCut_WithItsImagesKept_AndTheWholeOfItInAFile()
    {
        var text = "FIRST" + new string('x', ToolOutputs.MaxText) + "LAST";
        var servers = Servers(Answering($$$"""{"content":[{"type":"text","text":"{{{text}}}"},{"type":"image","data":"aGk=","mimeType":"image/png"}],"structuredContent":{"made":1}}"""));
        await servers.StartAsync(Plenty);

        var result = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");

        var saved = _folder.PathOf("session/outputs/toolu_1.json");
        var sent = result!.Raw!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(sent).StartsWith("FIRST");
        await Assert.That(sent).Contains($"the whole of it is in {saved}; read it in parts");
        await Assert.That(sent.Length).IsLessThan(ToolOutputs.MaxText + 500);
        await Assert.That(result.Raw!["content"]![1]!["data"]!.GetValue<string>()).IsEqualTo("aGk=");
        await Assert.That(result.Raw!.ContainsKey("structuredContent")).IsFalse();
        await Assert.That(JsonNode.Parse(File.ReadAllText(saved))!["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo(text);
    }

    [Test]
    public async Task ImagesAlone_AreNotCut()
    {
        var image = new string('i', ToolOutputs.MaxText * 2);
        var servers = Servers(Answering($$"""{"content":[{"type":"text","text":"a chart"},{"type":"image","data":"{{image}}","mimeType":"image/png"}]}"""));
        await servers.StartAsync(Plenty);

        var result = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");

        await Assert.That(result!.Raw!["content"]![1]!["data"]!.GetValue<string>()).IsEqualTo(image);
    }

    [Test]
    public async Task AResultTooLargeForTheJournal_IsAnsweredWhole_ToARepeatedId()
    {
        var image = new string('i', CallJournal.MaxText + 1);
        var stub = Answering($$"""{"content":[{"type":"image","data":"{{image}}","mimeType":"image/png"}]}""");
        var servers = Servers(stub);
        await servers.StartAsync(Plenty);

        var first = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");
        var calls = stub.ReceivedLines().Length;
        var again = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");

        await Assert.That(stub.ReceivedLines().Length).IsEqualTo(calls);
        await Assert.That(again!.Raw!.ToJsonString()).IsEqualTo(first!.Raw!.ToJsonString());
    }

    [Test]
    public async Task AHungCall_TimesOut_TellsTheServer_AndIsAnError()
    {
        var stub = new StubMcpServer(_folder) { Call = ":" };
        var servers = Servers(stub);
        await servers.StartAsync(Plenty);
        var time = new FakeTimeProvider();
        var journal = new CallJournal(_folder.PathOf("session/tools.jsonl"));
        _journals.Add(journal);
        var dispatcher = new ToolDispatcher(_folder.Context, [], journal, servers, time);

        var call = Call(dispatcher, "stub__create_entities", "toolu_1");
        await Eventually(() => stub.ReceivedLines().Any(line => line.Contains("tools/call", StringComparison.Ordinal)));
        time.Advance(ToolDispatcher.ExternalTimeout);
        var result = await call;

        await Assert.That(result).IsEqualTo(ToolResult.Error("Timed out after 600 s: the MCP server was told to stop, and may have done part of the call."));
        await Eventually(() => stub.ReceivedLines().Any(line => line.Contains("notifications/cancelled", StringComparison.Ordinal)));
        await Assert.That(File.ReadAllLines(_folder.PathOf("session/tools.jsonl"))[^1]).Contains("\"durationMs\":600000");
    }

    [Test]
    public async Task AServerThatDies_FailsTheCallWaiting_AndEveryLaterOne()
    {
        var servers = Servers(new StubMcpServer(_folder) { Call = "echo 'out of memory' >&2; exit 9" });
        await servers.StartAsync(Plenty);
        var dispatcher = Dispatcher(servers);

        var waiting = await Call(dispatcher, "stub__create_entities", "toolu_1");
        var later = await Call(dispatcher, "stub__create_entities", "toolu_2");

        const string Expected = "MCP server 'stub' has stopped (it exited with code 9): the call got no answer, and its tools can't be used again in this session.\nout of memory";
        await Assert.That(waiting).IsEqualTo(ToolResult.Error(Expected));
        await Assert.That(later).IsEqualTo(ToolResult.Error(Expected));
    }

    [Test]
    public async Task ACancelledCall_TellsTheServer_AndIsAnError()
    {
        var stub = new StubMcpServer(_folder) { Call = ":" };
        var servers = Servers(stub);
        await servers.StartAsync(Plenty);
        using var cancel = new CancellationTokenSource();

        var call = Call(Dispatcher(servers), "stub__create_entities", "toolu_1", cancelled: cancel.Token);
        await Eventually(() => stub.ReceivedLines().Any(line => line.Contains("tools/call", StringComparison.Ordinal)));
        await cancel.CancelAsync();
        var result = await call;

        await Assert.That(result).IsEqualTo(ToolResult.Error("Cancelled: the MCP server was told to stop, and may have done part of the call."));
        await Eventually(() => stub.ReceivedLines().Any(line => line.Contains("notifications/cancelled", StringComparison.Ordinal)));
        var sentId = JsonNode.Parse(stub.ReceivedLines().Single(line => line.Contains("tools/call", StringComparison.Ordinal)))!["id"]!.GetValue<long>();
        var cancelled = JsonNode.Parse(stub.ReceivedLines().Single(line => line.Contains("notifications/cancelled", StringComparison.Ordinal)))!;
        await Assert.That(cancelled["params"]!["requestId"]!.GetValue<long>()).IsEqualTo(sentId);
    }

    [Test]
    public async Task ACallTheStopCutsShort_IsNotAnswered_AndReadsAsInterrupted()
    {
        var stub = new StubMcpServer(_folder) { Call = ":" };
        var servers = Servers(stub);
        await servers.StartAsync(Plenty);
        using var stopping = new CancellationTokenSource();

        var call = Call(Dispatcher(servers), "stub__create_entities", "toolu_1", stopping: stopping.Token);
        await Eventually(() => stub.ReceivedLines().Any(line => line.Contains("tools/call", StringComparison.Ordinal)));
        await stopping.CancelAsync();
        var result = await call;
        var again = await Call(Dispatcher(servers), "stub__create_entities", "toolu_1");

        await Assert.That(result).IsNull();
        await Assert.That(again!.Text).Contains("was interrupted");
    }

    [Test]
    public async Task AToolOfNoServer_IsRefused_ListingTheSessionsTools()
    {
        var servers = Servers(new StubMcpServer(_folder));
        await servers.StartAsync(Plenty);

        var result = await Call(Dispatcher(servers, "arm_bash"), "stub__nope");

        await Assert.That(result).IsEqualTo(ToolResult.Error("There's no tool 'stub__nope' in this session. Its tools: arm_bash, stub__create_entities, stub__dotted_name."));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Plenty;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("It never happened.");
            }

            await Task.Delay(10);
        }
    }
}
