using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.Journal;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary>Calls through the dispatcher: only the session's tools, and each tool-use id at most once (the journal).</summary>
[UnsupportedOSPlatform("windows")]
public class ToolDispatcherTests
{
    private readonly ToolsTestFolder _folder = new();
    private readonly List<CallJournal> _journals = [];

    private string JournalPath => _folder.PathOf("session/tools.jsonl");

    [After(Test)]
    public void Cleanup()
    {
        _journals.ForEach(journal => journal.Dispose());
        _folder.Dispose();
    }

    /// <summary>A dispatcher as a fresh server has it: the journal read anew from its file.</summary>
    private ToolDispatcher Dispatcher(params string[] tools)
    {
        var journal = new CallJournal(JournalPath);
        _journals.Add(journal);
        return new ToolDispatcher(_folder.Context, tools.Length == 0 ? [.. ToolCatalog.All.Select(tool => tool.Name)] : tools, journal);
    }

    private static Task<ToolResult?> Call(ToolDispatcher dispatcher, string name, string arguments, string? id) =>
        dispatcher.CallAsync(name, JsonNode.Parse(arguments) as JsonObject, id, CancellationToken.None, CancellationToken.None);

    private const string Append = """{ "command": "echo ran >> runs.txt; echo done" }""";

    private int Runs() => File.Exists(_folder.PathOf("runs.txt")) ? File.ReadAllLines(_folder.PathOf("runs.txt")).Length : 0;

    [Test]
    public async Task Call_AToolTheSessionDidNotName_IsRefused()
    {
        var dispatcher = Dispatcher(ToolCatalog.FileRead);

        var result = await Call(dispatcher, ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).Contains("no tool 'arm_bash' in this session");
        await Assert.That(Runs()).IsEqualTo(0);
    }

    [Test]
    public async Task Call_AnIdThatFinished_ReturnsItsResultWithoutRunningAgain()
    {
        var first = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");

        // A new server (a restart, or a resumed agent's) reads the journal anew.
        var again = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(again).IsEqualTo(first);
        await Assert.That(Runs()).IsEqualTo(1);
    }

    [Test]
    public async Task Call_AnIdThatStartedAndNeverFinished_IsRefusedAsInterrupted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        await File.WriteAllTextAsync(JournalPath, """{"id":"toolu_1","event":"started","tool":"arm_bash","at":"2026-10-26T12:00:00+00:00"}""" + "\n");

        var result = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).Contains("was interrupted");
        await Assert.That(Runs()).IsEqualTo(0);
    }

    [Test]
    public async Task Call_WithoutAnId_RunsEveryTime()
    {
        var dispatcher = Dispatcher();

        await Call(dispatcher, ToolCatalog.Bash, Append, null);
        await Call(dispatcher, ToolCatalog.Bash, Append, null);

        await Assert.That(Runs()).IsEqualTo(2);
    }

    [Test]
    public async Task Journal_ATornLastLine_IsSkipped_AndTheRestStands()
    {
        await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");
        await File.AppendAllTextAsync(JournalPath, """{"id":"toolu_2","event":"fin""");

        var replayed = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");
        var fresh = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_3");

        await Assert.That(replayed!.Text).Contains("done");
        await Assert.That(fresh!.IsError).IsFalse();
        await Assert.That(Runs()).IsEqualTo(2);
        // The next entry started on a line of its own.
        await Assert.That(File.ReadAllLines(JournalPath).Count(line => line.Contains("toolu_3", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    public async Task Call_ThatStopsWithTheServer_StaysInterrupted()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopped = await Dispatcher().CallAsync(ToolCatalog.Bash, JsonNode.Parse("""{ "command": "sleep 30" }""") as JsonObject, "toolu_1", CancellationToken.None, stopping.Token);

        var later = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(stopped).IsNull();
        await Assert.That(later!.Text).Contains("was interrupted");
    }

    [Test]
    public async Task Call_AFailingFileTool_IsAResult()
    {
        var result = await Call(Dispatcher(), ToolCatalog.FileCreate, """{ "file_path": "/proc/arm-cannot-create-here/x", "content": "" }""", null);

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).StartsWith("arm_file_create failed:");
    }
}
