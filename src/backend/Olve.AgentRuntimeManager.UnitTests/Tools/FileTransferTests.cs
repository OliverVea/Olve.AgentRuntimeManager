using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools;
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task CopyOrMove_OntoAFolderThatExists_IsRefused_OverwriteOrNot(bool move)
    {
        _folder.Write("src/a.txt", "a");
        _folder.Write("dst/kept.txt", "kept");
        _folder.Write("file.txt", "f");

        var folder = move ? Move("""{ "file_path": "src", "destination_path": "dst", "overwrite": true }""") : Copy("""{ "file_path": "src", "destination_path": "dst", "overwrite": true }""");
        var file = move ? Move("""{ "file_path": "file.txt", "destination_path": "dst", "overwrite": true }""") : Copy("""{ "file_path": "file.txt", "destination_path": "dst", "overwrite": true }""");

        foreach (var result in (Olve.AgentRuntimeManager.Tools.Calls.ToolResult[])[folder, file])
        {
            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Text).Contains("arm_file_delete");
        }

        await Assert.That(File.Exists(_folder.PathOf("dst/kept.txt"))).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("src/a.txt"))).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("file.txt"))).IsTrue();
    }

    [Test]
    public async Task CopyOrMove_AFolderOntoAFile_IsRefused()
    {
        _folder.Write("src/a.txt", "a");
        var file = _folder.Write("file.txt", "f");

        var result = Move("""{ "file_path": "src", "destination_path": "file.txt", "overwrite": true }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("f");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CopyOrMove_ALinkOntoItsOwnTarget_IsRefused(bool move)
    {
        var target = _folder.Write("b", "content");
        File.CreateSymbolicLink(_folder.PathOf("a"), target);

        var result = move ? Move("""{ "file_path": "a", "destination_path": "b", "overwrite": true }""") : Copy("""{ "file_path": "a", "destination_path": "b", "overwrite": true }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("are the same file");
        await Assert.That(new FileInfo(target).LinkTarget).IsNull();
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("content");
    }

    [Test]
    public async Task Copy_OntoAHardLinkOfItself_IsRefused()
    {
        var file = _folder.Write("a", "content");
        using (var link = System.Diagnostics.Process.Start("ln", [file, _folder.PathOf("b")]))
        {
            await link.WaitForExitAsync();
        }

        var result = Copy("""{ "file_path": "a", "destination_path": "b", "overwrite": true }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("are the same file");
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("content");
    }

    [Test]
    public async Task Copy_OverwritingAFileWithALongName_FitsTheTemporaryName()
    {
        var name = new string('n', 250);
        _folder.Write("a.txt", "new");
        var target = _folder.Write(name, "old");

        var result = Copy($$"""{ "file_path": "a.txt", "destination_path": "{{name}}", "overwrite": true }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("new");
        await Assert.That(Directory.GetFileSystemEntries(_folder.Workplace).Length).IsEqualTo(2);
    }

    [Test]
    public async Task Copy_AFolderThatFailsPartway_LeavesNoPartialCopy()
    {
        _folder.Write("src/a.txt", "a");
        Directory.CreateDirectory(_folder.PathOf("src/z"));
        _folder.MakeFifo("src/z/fifo");

        await Assert.That(() => Copy("""{ "file_path": "src", "destination_path": "dst" }""")).Throws<IOException>().WithMessageContaining("a FIFO");

        await Assert.That(ToolPaths.Exists(_folder.PathOf("dst"))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Move_ToAnotherFilesystem_CopiesThenDeletesTheSource(bool folder)
    {
        // /dev/shm is a tmpfs: another filesystem than the temp folder's, where it is one.
        Skip.When(!Directory.Exists("/dev/shm") || Native.Identity("/dev/shm")?.Device == Native.Identity(_folder.Workplace)?.Device, "no second filesystem here");
        var elsewhere = Directory.CreateTempSubdirectory("arm-tools-shm-");
        try
        {
            var shm = Path.Combine("/dev/shm", Path.GetFileName(elsewhere.FullName));
            Directory.CreateDirectory(shm);
            try
            {
                if (folder)
                {
                    File.WriteAllText(Path.Combine(shm, "a.txt"), "a");
                }
                else
                {
                    File.WriteAllText(Path.Combine(shm, "file.txt"), "f");
                    _folder.Write("file.txt", "old");
                }

                var source = folder ? shm : Path.Combine(shm, "file.txt");
                var result = Move($$"""{ "file_path": "{{source}}", "destination_path": "{{(folder ? "moved" : "file.txt")}}", "overwrite": true }""");

                await Assert.That(result.IsError).IsFalse();
                await Assert.That(result.Text).Contains("another filesystem");
                await Assert.That(ToolPaths.Exists(source)).IsFalse();
                await Assert.That(await File.ReadAllTextAsync(_folder.PathOf(folder ? "moved/a.txt" : "file.txt"))).IsEqualTo(folder ? "a" : "f");
            }
            finally
            {
                if (Directory.Exists(shm))
                {
                    Directory.Delete(shm, recursive: true);
                }
            }
        }
        finally
        {
            elsewhere.Delete(recursive: true);
        }
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
    public async Task Move_OntoAFileThatExists_IsRefused_UnlessOverwrite()
    {
        _folder.Write("a.txt", "new");
        _folder.Write("dst.txt", "old");

        var refused = Move("""{ "file_path": "a.txt", "destination_path": "dst.txt" }""");
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("a.txt"))).IsTrue();

        var replaced = Move("""{ "file_path": "a.txt", "destination_path": "dst.txt", "overwrite": true }""");
        await Assert.That(replaced.IsError).IsFalse();
        await Assert.That(File.Exists(_folder.PathOf("a.txt"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(_folder.PathOf("dst.txt"))).IsEqualTo("new");
    }

    [Test]
    public async Task Move_ALinkToAFolder_OntoAFile_ReplacesTheFileWithTheLink()
    {
        var kept = _folder.Write("target/keep.txt", "x");
        Directory.CreateSymbolicLink(_folder.PathOf("dirlink"), _folder.PathOf("target"));
        _folder.Write("dest.txt", "old");

        var result = Move("""{ "file_path": "dirlink", "destination_path": "dest.txt", "overwrite": true }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(new FileInfo(_folder.PathOf("dest.txt")).LinkTarget).IsEqualTo(_folder.PathOf("target"));
        await Assert.That(ToolPaths.Exists(_folder.PathOf("dirlink"))).IsFalse();
        await Assert.That(File.Exists(kept)).IsTrue();
    }

    [Test]
    public async Task Move_ALinkToAFolder_MovesTheLink()
    {
        _folder.Write("target/keep.txt", "x");
        Directory.CreateSymbolicLink(_folder.PathOf("dirlink"), _folder.PathOf("target"));

        var result = Move("""{ "file_path": "dirlink", "destination_path": "moved" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(new FileInfo(_folder.PathOf("moved")).LinkTarget).IsEqualTo(_folder.PathOf("target"));
        await Assert.That(File.Exists(_folder.PathOf("target/keep.txt"))).IsTrue();
    }

    [Test]
    public async Task Move_ThatFails_LeavesTheFileItWouldReplace()
    {
        Skip.When(Supervisor.Protocol.Posix.GetEUid() == 0, "root may rename out of a read-only folder");

        _folder.Write("locked/a.txt", "a");
        var old = _folder.Write("dest.txt", "old");
        var locked = _folder.PathOf("locked");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = Move("""{ "file_path": "locked/a.txt", "destination_path": "dest.txt", "overwrite": true }""");

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(old)).IsEqualTo("old");
            await Assert.That(File.Exists(_folder.PathOf("locked/a.txt"))).IsTrue();
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task Copy_IntoItselfThroughALink_IsRefused()
    {
        _folder.Write("src/a.txt", "a");
        Directory.CreateSymbolicLink(_folder.PathOf("srclink"), _folder.PathOf("src"));

        var result = Copy("""{ "file_path": "src", "destination_path": "srclink/inner" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("into itself");
        await Assert.That(Directory.Exists(_folder.PathOf("src/inner"))).IsFalse();
    }

    [Test]
    public async Task Move_OverwritingWhatHoldsTheSourceThroughALink_IsRefused()
    {
        _folder.Write("dst/a.txt", "a");
        Directory.CreateSymbolicLink(_folder.PathOf("dlink"), _folder.PathOf("dst"));

        var result = Move("""{ "file_path": "dlink/a.txt", "destination_path": "dst", "overwrite": true }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(File.Exists(_folder.PathOf("dst/a.txt"))).IsTrue();
    }

    [Test]
    public async Task Copy_AFifo_IsRefused()
    {
        _folder.MakeFifo("fifo");

        var result = Copy("""{ "file_path": "fifo", "destination_path": "copy" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("a FIFO");
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
