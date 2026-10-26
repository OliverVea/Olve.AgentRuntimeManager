using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

[UnsupportedOSPlatform("windows")]
public class FileEditToolTests
{
    private readonly ToolsTestFolder _folder = new();

    [After(Test)]
    public void Cleanup() => _folder.Dispose();

    private Olve.AgentRuntimeManager.Tools.Calls.ToolResult Run(string json) => FileEditTool.Run(ToolsTestFolder.Arguments(json), _folder.Context);

    [Test]
    public async Task Run_AUniqueString_IsReplaced()
    {
        var path = _folder.Write("a.txt", "alpha beta gamma");

        var result = Run("""{ "file_path": "a.txt", "old_string": "beta", "new_string": "BETA" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("alpha BETA gamma");
    }

    [Test]
    public async Task Run_AStringThatIsNotThere_IsAnError()
    {
        var path = _folder.Write("a.txt", "alpha");

        var result = Run("""{ "file_path": "a.txt", "old_string": "Alpha", "new_string": "x" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("isn't in");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("alpha");
    }

    [Test]
    public async Task Run_AStringThatIsThereTwice_IsAnError_UnlessReplaceAll()
    {
        var path = _folder.Write("a.txt", "x-x");

        var refused = Run("""{ "file_path": "a.txt", "old_string": "x", "new_string": "y" }""");
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Text).Contains("2 times");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("x-x");

        var all = Run("""{ "file_path": "a.txt", "old_string": "x", "new_string": "y", "replace_all": true }""");
        await Assert.That(all.IsError).IsFalse();
        await Assert.That(all.Text).Contains("replaced all 2 occurrences");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("y-y");
    }

    [Test]
    public async Task Run_IdenticalStrings_AreAnError()
    {
        _folder.Write("a.txt", "x");

        var result = Run("""{ "file_path": "a.txt", "old_string": "x", "new_string": "x" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("the same");
    }

    [Test]
    public async Task Run_AnEmptyNewString_DeletesTheText()
    {
        var path = _folder.Write("a.txt", "keep drop keep");

        var result = Run("""{ "file_path": "a.txt", "old_string": " drop", "new_string": "" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("keep keep");
    }

    [Test]
    public async Task Run_KeepsAByteOrderMark()
    {
        var path = _folder.PathOf("bom.txt");
        await File.WriteAllBytesAsync(path, [.. Encoding.UTF8.Preamble, .. "old"u8.ToArray()]);

        Run("""{ "file_path": "bom.txt", "old_string": "old", "new_string": "new" }""");

        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo((byte[])[.. Encoding.UTF8.Preamble, .. "new"u8.ToArray()]);
    }

    [Test]
    public async Task Run_AFileThatIsNotUtf8_IsRefused_AndLeftAsItWas()
    {
        var path = _folder.PathOf("latin1.txt");
        byte[] bytes = [0x63, 0xE9, 0x0A]; // "cé\n" in Latin-1
        await File.WriteAllBytesAsync(path, bytes);

        var result = Run("""{ "file_path": "latin1.txt", "old_string": "c", "new_string": "d" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("isn't UTF-8");
        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(bytes);
    }

    [Test]
    public async Task Run_WritesInPlace_KeepingHardLinksAndMode()
    {
        var path = _folder.Write("script.sh", "echo old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var other = _folder.PathOf("hardlink.sh");
        using (var link = System.Diagnostics.Process.Start("ln", [path, other]))
        {
            await link.WaitForExitAsync();
        }

        Run("""{ "file_path": "script.sh", "old_string": "old", "new_string": "new" }""");

        await Assert.That(await File.ReadAllTextAsync(other)).IsEqualTo("echo new");
        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task Run_InAReadOnlyFolder_Works()
    {
        Skip.When(Supervisor.Protocol.Posix.GetEUid() == 0, "root writes anywhere");
        var path = _folder.Write("ro/a.txt", "old");
        var folder = _folder.PathOf("ro");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = Run("""{ "file_path": "ro/a.txt", "old_string": "old", "new_string": "new" }""");

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new");
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task Run_ThroughALink_EditsWhatItPointsTo_AndKeepsTheLink()
    {
        var target = _folder.Write("real.txt", "a");
        var link = _folder.PathOf("link.txt");
        File.CreateSymbolicLink(link, target);

        var result = Run("""{ "file_path": "link.txt", "old_string": "a", "new_string": "b" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("b");
    }

    [Test]
    public async Task Run_AFifo_IsRefused_WithoutBlocking()
    {
        _folder.MakeFifo("fifo");

        var result = Run("""{ "file_path": "fifo", "old_string": "a", "new_string": "b" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("isn't a regular file");
    }

    [Test]
    public async Task Run_AMissingFile_IsAnError()
    {
        var result = Run("""{ "file_path": "nope.txt", "old_string": "a", "new_string": "b" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("arm_file_create");
    }
}
