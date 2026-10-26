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
    public async Task Run_AMissingFile_IsAnError()
    {
        var result = Run("""{ "file_path": "nope.txt", "old_string": "a", "new_string": "b" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("arm_file_create");
    }
}
