using System.Diagnostics;
using System.Runtime.Versioning;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Supervision;

/// <summary>
/// The supervisor's <c>remove-folder</c> (#22), through the built binary: what it will and won't
/// empty. That a link in the folder isn't followed is in <see cref="AgentUserTests"/>, with ARM's part.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class RemoveFolderTests
{
    private static readonly string Supervisor = Path.Combine(AppContext.BaseDirectory, "olve-arm-supervisor");

    private readonly string _root = Directory.CreateTempSubdirectory("arm-remove-folder-").FullName;

    [After(Test)]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [Test]
    [Arguments("relative/sessions", "6f1c2a8e-3b4d-4e5f-8a9b-0c1d2e3f4a5b")]
    [Arguments("/abs", "")]
    [Arguments("/abs", "..")]
    [Arguments("/abs", "../x")]
    [Arguments("/abs", "6f1c2a8e3b4d4e5f8a9b0c1d2e3f4a5b")]
    [Arguments("/abs", "00000000-0000-0000-0000-000000000000")]
    public async Task BadArguments_Exit2(string sessions, string id)
    {
        var (exitCode, error) = await Run("remove-folder", sessions, id);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error).Contains("remove-folder <sessions folder> <session id>");
    }

    [Test]
    public async Task EmptiesOnlyTheRetiredFolder_KeepingItAndTheLiveSessions()
    {
        var id = Guid.NewGuid();
        var live = Path.Combine(_root, id.ToString());
        var retired = Path.Combine(_root, $".deleted-{id:D}");
        Directory.CreateDirectory(Path.Combine(live, "outputs"));
        File.WriteAllText(Path.Combine(live, "outputs", "a.txt"), "live");
        Directory.CreateDirectory(Path.Combine(retired, "work", "deep"));
        File.WriteAllText(Path.Combine(retired, "work", "deep", "b.txt"), "deleted");

        var (exitCode, _) = await Run("remove-folder", _root, id.ToString("D"));
        var (again, _) = await Run("remove-folder", _root, id.ToString("D"));

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(again).IsEqualTo(0);
        await Assert.That(Directory.Exists(retired)).IsTrue(); // ARM removes the empty folder
        await Assert.That(Directory.GetFileSystemEntries(retired)).IsEmpty();
        await Assert.That(File.ReadAllText(Path.Combine(live, "outputs", "a.txt"))).IsEqualTo("live");
    }

    [Test]
    public async Task ARetiredNameThatsALink_IsLeftAlone()
    {
        var id = Guid.NewGuid();
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(_root, $".deleted-{id:D}"), target);

        var (exitCode, error) = await Run("remove-folder", _root, id.ToString("D"));

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(error).Contains("isn't a folder; left as it is.");
        await Assert.That(File.ReadAllText(Path.Combine(target, "keep.txt"))).IsEqualTo("keep");
    }

    [Test]
    [Arguments("relative/projects", "/s/6f1c2a8e-3b4d-4e5f-8a9b-0c1d2e3f4a5b/work")]
    [Arguments("/abs/projects", "relative/6f1c2a8e-3b4d-4e5f-8a9b-0c1d2e3f4a5b/work")]
    [Arguments("/abs/projects", "/s/6f1c2a8e-3b4d-4e5f-8a9b-0c1d2e3f4a5b")]
    [Arguments("/abs/projects", "/s/6f1c2a8e-3b4d-4e5f-8a9b-0c1d2e3f4a5b/work/x")]
    [Arguments("/abs/projects", "/s/not-a-session/work")]
    [Arguments("/abs/projects", "/s/00000000-0000-0000-0000-000000000000/work")]
    [Arguments("/abs/projects", "/work")]
    public async Task RemoveTranscripts_BadArguments_Exit2(string projects, string workplace)
    {
        var (exitCode, error) = await Run("remove-transcripts", projects, workplace);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error).Contains("remove-transcripts <claude projects folder> <workplace>");
    }

    [Test]
    public async Task RemoveTranscripts_RemovesOnlyThatWorkplacesFolder_LinksNotFollowed()
    {
        var projects = Path.Combine(_root, "projects");
        var workplace = $"/var/lib/olve-arm/sessions/{Guid.NewGuid():D}/work";
        var folder = Path.Combine(projects, ClaudeProjectsName(workplace));
        var other = Path.Combine(projects, ClaudeProjectsName($"/var/lib/olve-arm/sessions/{Guid.NewGuid():D}/work"));
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(folder, "subagents"));
        File.WriteAllText(Path.Combine(folder, "subagents", "a.jsonl"), "{}");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(folder, "link"), target);

        var (exitCode, _) = await Run("remove-transcripts", projects, workplace);
        var (again, _) = await Run("remove-transcripts", projects, workplace);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(again).IsEqualTo(0);
        await Assert.That(Directory.Exists(folder)).IsFalse();
        await Assert.That(Directory.Exists(other)).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(target, "keep.txt"))).IsEqualTo("keep");
    }

    [Test]
    public async Task ClaudeProjects_NamesAFolderAsClaudeCodeDoes() =>
        // As found on beta's VM (Claude Code 2.1.316): ~/.claude/projects/-var-lib-olve-arm-sessions-<id>-work.
        await Assert.That(ClaudeProjectsName("/var/lib/olve-arm/sessions/f3254bf4-799e-4409-a6a1-a64e349b38d8/work"))
            .IsEqualTo("-var-lib-olve-arm-sessions-f3254bf4-799e-4409-a6a1-a64e349b38d8-work");

    private static string ClaudeProjectsName(string workplace) => Olve.AgentRuntimeManager.Supervisor.Protocol.ClaudeProjects.FolderName(workplace);

    private static async Task<(int ExitCode, string Error)> Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Supervisor) { RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await error);
    }
}
