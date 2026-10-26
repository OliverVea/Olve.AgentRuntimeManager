using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary><c>arm_file_copy</c> and <c>arm_file_move</c>.</summary>
[UnsupportedOSPlatform("windows")]
public class FileTransferTests
{
    private readonly ToolsTestFolder _folder = new();

    [After(Test)]
    public void Cleanup() => _folder.Dispose();

    private Olve.AgentRuntimeManager.Tools.Calls.ToolResult Copy(string json) => FileTransfer.Copy(ToolsTestFolder.Arguments(json), _folder.Context);

    private Olve.AgentRuntimeManager.Tools.Calls.ToolResult Move(string json) => FileTransfer.Move(ToolsTestFolder.Arguments(json), _folder.Context);

    [Test]
    public async Task Copy_AFile_ToANewPath_MakingItsFolders()
    {
        _folder.Write("a.txt", "x");

        var result = Copy("""{ "file_path": "a.txt", "destination_path": "out/b.txt" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("out/b.txt"))).IsEqualTo("x");
        await Assert.That(File.Exists(_folder.PathOf("a.txt"))).IsTrue();
    }

    [Test]
    public async Task Copy_AFolder_WithEverythingInIt_LinksAsLinks()
    {
        _folder.Write("src/a.txt", "a");
        _folder.Write("src/sub/b.txt", "b");
        File.CreateSymbolicLink(_folder.PathOf("src/link"), "a.txt");

        var result = Copy("""{ "file_path": "src", "destination_path": "dst" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("dst/a.txt"))).IsEqualTo("a");
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("dst/sub/b.txt"))).IsEqualTo("b");
        await Assert.That(new FileInfo(_folder.PathOf("dst/link")).LinkTarget).IsEqualTo("a.txt");
    }

    [Test]
    public async Task Copy_OntoSomethingThatExists_IsRefused_UnlessOverwrite()
    {
        _folder.Write("a.txt", "new");
        var target = _folder.Write("b.txt", "old");

        var refused = Copy("""{ "file_path": "a.txt", "destination_path": "b.txt" }""");
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Text).Contains("set `overwrite`");
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("old");

        var replaced = Copy("""{ "file_path": "a.txt", "destination_path": "b.txt", "overwrite": true }""");
        await Assert.That(replaced.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("new");
    }

    [Test]
    public async Task Copy_OverwritingAFolder_ReplacesItWhole()
    {
        _folder.Write("src/a.txt", "a");
        _folder.Write("dst/stale.txt", "stale");

        var result = Copy("""{ "file_path": "src", "destination_path": "dst", "overwrite": true }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(File.Exists(_folder.PathOf("dst/a.txt"))).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("dst/stale.txt"))).IsFalse();
    }

    [Test]
    public async Task Copy_AFolderIntoItself_IsRefused()
    {
        _folder.Write("src/a.txt", "a");

        var result = Copy("""{ "file_path": "src", "destination_path": "src/inner" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("into itself");
    }

    [Test]
    public async Task Copy_SomethingMissing_IsAnError()
    {
        var result = Copy("""{ "file_path": "nope", "destination_path": "b" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("doesn't exist");
    }

    [Test]
    public async Task Move_AFile_Renames()
    {
        _folder.Write("a.txt", "x");

        var result = Move("""{ "file_path": "a.txt", "destination_path": "b.txt" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(File.Exists(_folder.PathOf("a.txt"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("b.txt"))).IsEqualTo("x");
    }

    [Test]
    public async Task Move_AFolder_ToANewPath()
    {
        _folder.Write("src/sub/a.txt", "a");

        var result = Move("""{ "file_path": "src", "destination_path": "elsewhere/dst" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(Directory.Exists(_folder.PathOf("src"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("elsewhere/dst/sub/a.txt"))).IsEqualTo("a");
    }

    [Test]
    public async Task Move_OntoSomethingThatExists_IsRefused_UnlessOverwrite()
    {
        _folder.Write("a.txt", "new");
        _folder.Write("dst/old.txt", "old");

        var refused = Move("""{ "file_path": "a.txt", "destination_path": "dst" }""");
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("a.txt"))).IsTrue();

        var replaced = Move("""{ "file_path": "a.txt", "destination_path": "dst", "overwrite": true }""");
        await Assert.That(replaced.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("dst"))).IsEqualTo("new");
    }

    [Test]
    public async Task Move_OverwritingAFolderThatHoldsTheSource_IsRefused()
    {
        _folder.Write("dst/a.txt", "a");

        var result = Move("""{ "file_path": "dst/a.txt", "destination_path": "dst", "overwrite": true }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("dst/a.txt"))).IsTrue();
    }
}
