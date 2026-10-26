using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary><c>arm_file_create</c> and <c>arm_file_delete</c>.</summary>
[UnsupportedOSPlatform("windows")]
public class FileCreateAndDeleteToolTests
{
    private readonly ToolsTestFolder _folder = new();

    [After(Test)]
    public void Cleanup() => _folder.Dispose();

    private Olve.AgentRuntimeManager.Tools.Calls.ToolResult Create(string json) => FileCreateTool.Run(ToolsTestFolder.Arguments(json), _folder.Context);

    private Olve.AgentRuntimeManager.Tools.Calls.ToolResult Delete(string json) => FileDeleteTool.Run(ToolsTestFolder.Arguments(json), _folder.Context);

    [Test]
    public async Task Create_MakesTheFileAndItsFolders()
    {
        var result = Create("""{ "file_path": "new/deep/a.txt", "content": "hello\nworld\n" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Text).Contains("(2 lines, 12 bytes)");
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("new/deep/a.txt"))).IsEqualTo("hello\nworld\n");
    }

    [Test]
    public async Task Create_AnEmptyFile_IsFine()
    {
        var result = Create("""{ "file_path": "empty.txt", "content": "" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(new FileInfo(_folder.PathOf("empty.txt")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Create_AFileThatExists_IsRefused()
    {
        var path = _folder.Write("a.txt", "before");

        var result = Create("""{ "file_path": "a.txt", "content": "after" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("exists");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("before");
    }

    [Test]
    public async Task Create_OverADanglingLink_IsRefused()
    {
        File.CreateSymbolicLink(_folder.PathOf("link"), _folder.PathOf("target"));

        var result = Create("""{ "file_path": "link", "content": "x" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("target"))).IsFalse();
    }

    [Test]
    public async Task Delete_AFile()
    {
        var path = _folder.Write("a.txt", "x");

        var result = Delete("""{ "file_path": "a.txt" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Delete_AFolder_OnlyWithRecursive()
    {
        _folder.Write("dir/inner/a.txt", "x");

        var refused = Delete("""{ "file_path": "dir" }""");
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Text).Contains("set `recursive`");
        await Assert.That(Directory.Exists(_folder.PathOf("dir"))).IsTrue();

        var deleted = Delete("""{ "file_path": "dir", "recursive": true }""");
        await Assert.That(deleted.IsError).IsFalse();
        await Assert.That(Directory.Exists(_folder.PathOf("dir"))).IsFalse();
    }

    [Test]
    public async Task Delete_ALinkToAFolder_DeletesTheLinkOnly()
    {
        var target = _folder.Write("target/keep.txt", "x");
        Directory.CreateSymbolicLink(_folder.PathOf("link"), _folder.PathOf("target"));

        var result = Delete("""{ "file_path": "link", "recursive": true }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(ToolPaths.Exists(_folder.PathOf("link"))).IsFalse();
        await Assert.That(File.Exists(target)).IsTrue();
    }

    [Test]
    public async Task Delete_SomethingMissing_IsAnError()
    {
        var result = Delete("""{ "file_path": "nope" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("doesn't exist");
    }
}
