using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

[UnsupportedOSPlatform("windows")]
public class FileReadToolTests
{
    private readonly ToolsTestFolder _folder = new();

    [After(Test)]
    public void Cleanup() => _folder.Dispose();

    private Olve.AgentRuntimeManager.Tools.Calls.ToolResult Run(string json) => FileReadTool.Run(ToolsTestFolder.Arguments(json), _folder.Context);

    [Test]
    public async Task Run_ShowsNumberedLines()
    {
        _folder.Write("a.txt", "one\ntwo\n");

        var result = Run("""{ "file_path": "a.txt" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Text).IsEqualTo("     1\tone\n     2\ttwo");
    }

    [Test]
    public async Task Run_FromOffset_AtMostLimitLines_SaysWhereToReadOn()
    {
        _folder.Write("a.txt", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")));

        var result = Run("""{ "file_path": "a.txt", "offset": 3, "limit": 2 }""");

        await Assert.That(result.Text).IsEqualTo("     3\tline 3\n     4\tline 4\n[More lines follow line 4: read on with offset 5.]");
    }

    [Test]
    public async Task Run_AnAbsolutePath_IsReadAsGiven()
    {
        var path = _folder.Write("deep/a.txt", "x");

        var result = Run($$"""{ "file_path": "{{path}}" }""");

        await Assert.That(result.Text).IsEqualTo("     1\tx");
    }

    [Test]
    public async Task Run_AnOffsetPastTheEnd_SaysSo()
    {
        _folder.Write("a.txt", "one\n");

        var result = Run("""{ "file_path": "a.txt", "offset": 5 }""");

        await Assert.That(result.Text).Contains("has 1 lines: offset 5 is past its end");
    }

    [Test]
    public async Task Run_LongLines_AreCut()
    {
        _folder.Write("a.txt", new string('x', FileReadTool.MaxLineLength + 10));

        var result = Run("""{ "file_path": "a.txt" }""");

        await Assert.That(result.Text).Contains($"[... line cut: {FileReadTool.MaxLineLength + 10} characters]");
    }

    [Test]
    public async Task Run_AReadPastTheOutputCap_StopsAndSaysWhereToReadOn()
    {
        _folder.Write("a.txt", string.Join('\n', Enumerable.Range(1, 200).Select(_ => new string('y', 1000))));

        var result = Run("""{ "file_path": "a.txt" }""");

        await Assert.That(result.Text.Length).IsLessThan(FileReadTool.MaxOutput + 200);
        await Assert.That(result.Text).Contains("[Output cap reached after line");
    }

    [Test]
    public async Task Run_AFolder_IsRefused()
    {
        Directory.CreateDirectory(_folder.PathOf("dir"));

        var result = Run("""{ "file_path": "dir" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("is a folder");
    }

    [Test]
    public async Task Run_ABinaryFile_IsRefused()
    {
        File.WriteAllBytes(_folder.PathOf("b.bin"), [1, 2, 0, 3]);

        var result = Run("""{ "file_path": "b.bin" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("binary file");
    }

    [Test]
    public async Task Run_AMissingFile_IsAnError()
    {
        var result = Run("""{ "file_path": "nope.txt" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("doesn't exist");
    }
}
