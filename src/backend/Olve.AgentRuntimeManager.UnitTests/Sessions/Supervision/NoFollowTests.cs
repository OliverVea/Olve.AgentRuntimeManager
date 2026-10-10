using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Supervision;

/// <summary>
/// Opening without following a link (docs/AGENT-USER.md). <c>O_NOFOLLOW</c> isn't trusted alone:
/// under gVisor it opens a link's target. These tests hand the verification (<see cref="Posix.IsAt"/>)
/// a descriptor already opened on the target, so they hold whatever the kernel's open() does.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class NoFollowTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("arm-nofollow-").FullName;

    [After(Test)]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task IsAt_ADirectoryHandle_ForALinkToIt_IsRefused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        using var handle = Posix.OpenNoFollow(target, directory: true)!;

        await Assert.That(Posix.IsAt(handle, link, directory: true)).IsFalse();
        await Assert.That(Posix.IsAt(handle, target, directory: true)).IsTrue();
    }

    [Test]
    public async Task IsAt_AFileHandle_ForALinkToIt_IsRefused()
    {
        var target = Path.Combine(_root, "target.jsonl");
        await File.WriteAllTextAsync(target, "{}\n");
        var link = Path.Combine(_root, "output.jsonl");
        File.CreateSymbolicLink(link, target);
        using var handle = File.OpenHandle(target);

        await Assert.That(Posix.IsAt(handle, link, directory: false)).IsFalse();
        await Assert.That(Posix.IsAt(handle, target, directory: false)).IsTrue();
    }

    [Test]
    public async Task IsAt_AFileSwappedAfterTheOpen_IsRefused()
    {
        var path = Path.Combine(_root, "exit.json");
        var other = Path.Combine(_root, "other.json");
        await File.WriteAllTextAsync(path, "{}");
        await File.WriteAllTextAsync(other, "{}");
        using var handle = File.OpenHandle(path);

        File.Move(other, path, overwrite: true);

        await Assert.That(Posix.IsAt(handle, path, directory: false)).IsFalse();
    }

    [Test]
    public async Task IsAt_TheWrongKind_IsRefused()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        var file = Path.Combine(_root, "file");
        await File.WriteAllTextAsync(file, "");
        using var folderHandle = Posix.OpenNoFollow(folder, directory: true)!;
        using var fileHandle = File.OpenHandle(file);

        await Assert.That(Posix.IsAt(folderHandle, folder, directory: false)).IsFalse();
        await Assert.That(Posix.IsAt(fileHandle, file, directory: true)).IsFalse();
    }

    [Test]
    public async Task OpenNoFollow_ALink_IsNull_ARealOne_Opens()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        var file = Path.Combine(_root, "file");
        await File.WriteAllTextAsync(file, "x");
        var fileLink = Path.Combine(_root, "file-link");
        File.CreateSymbolicLink(fileLink, file);

        await Assert.That(Posix.OpenNoFollow(link, directory: true)).IsNull();
        await Assert.That(Posix.OpenNoFollow(fileLink)).IsNull();
        await Assert.That(Posix.OpenNoFollow(target)).IsNull();
        using var opened = Posix.OpenNoFollow(target, directory: true);
        await Assert.That(opened).IsNotNull();
        using var stream = Posix.OpenRegularFile(file);
        await Assert.That(stream).IsNotNull();
    }

    [Test]
    public async Task ExistsNoFollow_ADanglingLink_Exists_NothingDoesnt()
    {
        var link = Path.Combine(_root, "dangling");
        File.CreateSymbolicLink(link, Path.Combine(_root, "gone"));

        await Assert.That(Posix.ExistsNoFollow(link)).IsTrue();
        await Assert.That(Posix.ExistsNoFollow(Path.Combine(_root, "gone"))).IsFalse();
    }
}
