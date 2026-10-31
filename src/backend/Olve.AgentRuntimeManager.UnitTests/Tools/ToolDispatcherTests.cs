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
        using var stopping = new CancellationTokenSource();
        // The call is journaled before it first yields; a stop any earlier means it never started.
        var call = Dispatcher().CallAsync(ToolCatalog.Bash, JsonNode.Parse("""{ "command": "sleep 30" }""") as JsonObject, "toolu_1", CancellationToken.None, stopping.Token);
        await stopping.CancelAsync();
        var stopped = await call;

        var later = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(stopped).IsNull();
        await Assert.That(later!.Text).Contains("was interrupted");
    }

    [Test]
    public async Task Call_OnceTheServerStops_IsNotRun()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var result = await Dispatcher().CallAsync(ToolCatalog.Bash, JsonNode.Parse(Append) as JsonObject, "toolu_1", CancellationToken.None, stopping.Token);

        await Assert.That(result).IsNull();
        await Assert.That(Runs()).IsEqualTo(0);
        await Assert.That(File.ReadAllText(JournalPath)).DoesNotContain("toolu_1");
    }

    [Test]
    public async Task Call_ThatCantBeJournaled_IsNotRun()
    {
        var dispatcher = Dispatcher();
        _journals[^1].Dispose();

        var result = await Call(dispatcher, ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).Contains("the journal can't be written");
        await Assert.That(Runs()).IsEqualTo(0);
    }

    [Test]
    public async Task Journal_AResultThatCantBeWritten_IsStillReturned()
    {
        var journal = new CallJournal(JournalPath);
        _journals.Add(journal);
        await Assert.That(journal.Begin("toolu_1", ToolCatalog.Bash)).IsNull();
        journal.Dispose();

        journal.Finish("toolu_1", ToolCatalog.Bash, ToolResult.Ok("done"));

        // Not thrown; and the id now reads as interrupted, not running.
        await Assert.That(journal.Begin("toolu_1", ToolCatalog.Bash)!.Text).Contains("was interrupted");
    }

    [Test]
    public async Task Journal_ALineWithoutAnId_IsSkipped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        await File.WriteAllTextAsync(JournalPath, """{"event":"finished"}""" + "\n" + """{"id":"toolu_1","tool":"arm_bash"}""" + "\n");

        var result = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1");

        await Assert.That(result!.IsError).IsFalse();
        await Assert.That(Runs()).IsEqualTo(1);
    }

    [Test]
    public async Task Journal_TwoServersOnOneFile_KeepEachOthersLines()
    {
        var first = Dispatcher();
        var second = Dispatcher();

        await Call(first, ToolCatalog.Bash, Append, "toolu_1");
        await Call(second, ToolCatalog.Bash, Append, "toolu_2");
        await Call(first, ToolCatalog.Bash, Append, "toolu_3");

        var fresh = Dispatcher();
        foreach (var id in (string[])["toolu_1", "toolu_2", "toolu_3"])
        {
            await Assert.That((await Call(fresh, ToolCatalog.Bash, Append, id))!.Text).Contains("done");
        }

        await Assert.That(Runs()).IsEqualTo(3);
        await Assert.That(File.ReadAllLines(JournalPath).Length).IsEqualTo(6);
    }

    [Test]
    [Arguments("{ \"file_path\": \"a\\u0000b\" }")]
    [Arguments("{ \"file_path\": \"\\ud800\" }")]
    public async Task Call_ThatThrowsAnything_IsAnErrorResult_JournaledAsFinished(string arguments)
    {
        var result = await Call(Dispatcher(), ToolCatalog.FileRead, arguments, "toolu_1");
        var again = await Call(Dispatcher(), ToolCatalog.FileRead, arguments, "toolu_1");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).StartsWith("arm_file_read failed:");
        // Finished with that error, not left running nor interrupted.
        await Assert.That(again).IsEqualTo(result);
    }

    [Test]
    public async Task Journal_AfterAWriteThatFailedPartway_StartsOnAFreshLine()
    {
        var fail = true;
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var journal = new CallJournal(JournalPath, write: (file, bytes) =>
        {
            if (fail)
            {
                // Half the line reaches the file, then the disk fails.
                Olve.AgentRuntimeManager.Tools.Native.WriteAll(file, bytes[..(bytes.Length / 2)]);
                throw new IOException("disk full");
            }

            Olve.AgentRuntimeManager.Tools.Native.WriteAll(file, bytes);
        });
        _journals.Add(journal);

        await Assert.That(() => journal.Begin("toolu_1", ToolCatalog.Bash)).Throws<IOException>();
        fail = false;
        await Assert.That(journal.Begin("toolu_2", ToolCatalog.Bash)).IsNull();
        journal.Finish("toolu_2", ToolCatalog.Bash, ToolResult.Ok("done"));

        var replayed = await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_2");
        await Assert.That(replayed).IsEqualTo(ToolResult.Ok("done"));
        // toolu_1 was never written down whole: it isn't known, so it runs.
        await Assert.That((await Call(Dispatcher(), ToolCatalog.Bash, Append, "toolu_1"))!.Text).Contains("done");
        await Assert.That(Runs()).IsEqualTo(1);
    }

    [Test]
    public async Task Call_AFailingFileTool_IsAResult()
    {
        var result = await Call(Dispatcher(), ToolCatalog.FileCreate, """{ "file_path": "/proc/arm-cannot-create-here/x", "content": "" }""", null);

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).StartsWith("arm_file_create failed:");
    }
}
