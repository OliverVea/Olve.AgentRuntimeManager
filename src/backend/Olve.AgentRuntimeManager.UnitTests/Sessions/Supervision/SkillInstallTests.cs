using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Supervision;

/// <summary>
/// The supervisor's part of the skills registry (M10b), through the built binary: <c>skill-install</c>
/// from a local git repository (no network), <c>skill-remove</c>, and a launch copying the session's
/// skills into its folder before the agent starts.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class SkillInstallTests
{
    private const string Pdf = "---\nname: pdf\ndescription: Work with PDFs.\n---\n\n# PDF\n\nDo the thing.\n";
    private static readonly string Supervisor = Path.Combine(AppContext.BaseDirectory, "olve-arm-supervisor");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("arm-skills-").FullName;

    private string Registry => Path.Combine(_root, "registry");

    /// <summary>The names of what is in <paramref name="folder"/>.</summary>
    private static string[] Entries(string folder) => [.. Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)!)];

    [After(Test)]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>A repository with a <c>SKILL.md</c> (and the README every origin has) on <c>main</c>.</summary>
    private string SkillRepo(string name = "skill", string skillFile = Pdf)
    {
        var repo = TestGit.Origin(Path.Combine(_root, name));
        TestGit.Commit(repo, "SKILL.md", skillFile);
        return repo;
    }

    private SupervisorSkillInstall Install(string url, string name = "pdf", string? reference = null, string? path = null) => new()
    {
        Root = Registry,
        Name = name,
        Url = url,
        Ref = reference,
        Path = path,
        Environment = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? _root,
        },
        GitTimeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>Runs <c>skill-install</c> with <paramref name="install"/> on its stdin.</summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunInstall(SupervisorSkillInstall install) =>
        await Run(JsonSerializer.Serialize(install, SupervisorJsonContext.Default.SupervisorSkillInstall), "skill-install");

    private static async Task<(int ExitCode, string Output, string Error)> Run(string? input, params string[] arguments)
    {
        var info = new ProcessStartInfo(Supervisor)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Guard);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    [Test]
    public async Task Install_PutsTheRepositorysFolderAtRootName_WithoutGit_AndPrintsTheSkillFile()
    {
        var repo = SkillRepo();

        var (exitCode, output, error) = await RunInstall(Install(repo));

        await Assert.That(error).IsEqualTo("");
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).IsEqualTo(Pdf);
        var installed = Path.Combine(Registry, "pdf");
        await Assert.That(File.ReadAllText(Path.Combine(installed, "SKILL.md"))).IsEqualTo(Pdf);
        await Assert.That(File.Exists(Path.Combine(installed, "README.md"))).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(installed, ".git"))).IsFalse();
        // Nothing else: no temporary folder left in the registry.
        await Assert.That(Entries(Registry)).IsEquivalentTo(["pdf"]);
    }

    [Test]
    public async Task Install_Again_ReplacesTheWholeFolder()
    {
        var repo = SkillRepo();
        await RunInstall(Install(repo));
        await File.WriteAllTextAsync(Path.Combine(Registry, "pdf", "stale.txt"), "from before");
        var updated = Pdf.Replace("Do the thing.", "Do the new thing.");
        TestGit.Commit(repo, "SKILL.md", updated);
        TestGit.Commit(repo, "scripts.md", "new file\n");

        var (exitCode, output, _) = await RunInstall(Install(repo));

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).IsEqualTo(updated);
        await Assert.That(File.ReadAllText(Path.Combine(Registry, "pdf", "SKILL.md"))).IsEqualTo(updated);
        await Assert.That(File.Exists(Path.Combine(Registry, "pdf", "scripts.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(Registry, "pdf", "stale.txt"))).IsFalse();
        await Assert.That(Entries(Registry)).IsEquivalentTo(["pdf"]);
    }

    [Test]
    [Arguments("tag")]
    [Arguments("branch")]
    public async Task Install_ARef_TakesThatCommit(string kind)
    {
        var repo = SkillRepo();
        TestGit.Run(repo, kind == "tag" ? "tag" : "branch", "v1");
        TestGit.Commit(repo, "SKILL.md", Pdf.Replace("Do the thing.", "Do the later thing."));

        var (exitCode, output, error) = await RunInstall(Install(repo, reference: "v1"));

        await Assert.That(error).IsEqualTo("");
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).IsEqualTo(Pdf);
    }

    [Test]
    public async Task Install_AFileUrl_Works()
    {
        var repo = SkillRepo();

        var (exitCode, _, error) = await RunInstall(Install($"file://{repo}"));

        await Assert.That(error).IsEqualTo("");
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(Registry, "pdf", "SKILL.md"))).IsTrue();
    }

    [Test]
    public async Task Install_APath_TakesThatFolderOnly()
    {
        var repo = TestGit.Origin(Path.Combine(_root, "skills"));
        Directory.CreateDirectory(Path.Combine(repo, "skills", "pdf", "scripts"));
        TestGit.Commit(repo, "skills/pdf/SKILL.md", Pdf);
        TestGit.Commit(repo, "skills/pdf/scripts/fill.py", "print('fill')\n");

        var (exitCode, output, error) = await RunInstall(Install(repo, path: "skills/pdf"));

        await Assert.That(error).IsEqualTo("");
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).IsEqualTo(Pdf);
        await Assert.That(Entries(Path.Combine(Registry, "pdf"))).IsEquivalentTo(["SKILL.md", "scripts"]);
        await Assert.That(File.Exists(Path.Combine(Registry, "pdf", "scripts", "fill.py"))).IsTrue();
        await Assert.That(Entries(Registry)).IsEquivalentTo(["pdf"]);
    }

    [Test]
    public async Task Install_NoSkillFile_Exits3_AndLeavesNothing()
    {
        var repo = TestGit.Origin(Path.Combine(_root, "plain"));

        var (exitCode, output, error) = await RunInstall(Install(repo));

        await Assert.That(exitCode).IsEqualTo(3);
        await Assert.That(output).IsEqualTo("");
        await Assert.That(error.Trim()).IsEqualTo($"no SKILL.md in the root of {repo}");
        await Assert.That(Directory.GetFileSystemEntries(Registry)).IsEmpty();
    }

    [Test]
    public async Task Install_APathWithoutASkillFile_Exits3_NamingThePath()
    {
        var repo = SkillRepo();

        var (exitCode, _, error) = await RunInstall(Install(repo, path: "skills/nope"));

        await Assert.That(exitCode).IsEqualTo(3);
        await Assert.That(error.Trim()).IsEqualTo($"no SKILL.md in skills/nope of {repo}");
    }

    [Test]
    public async Task Install_AnInvalidSkillFile_Exits3_WithTheParsersError_AndTheOldInstallStands()
    {
        var repo = SkillRepo();
        await RunInstall(Install(repo));
        TestGit.Commit(repo, "SKILL.md", "---\nname: pdf\n---\nno description\n");

        var (exitCode, _, error) = await RunInstall(Install(repo));

        await Assert.That(exitCode).IsEqualTo(3);
        await Assert.That(error.Trim()).IsEqualTo("SKILL.md's frontmatter has no 'description'.");
        await Assert.That(File.ReadAllText(Path.Combine(Registry, "pdf", "SKILL.md"))).IsEqualTo(Pdf);
        await Assert.That(Entries(Registry)).IsEquivalentTo(["pdf"]);
    }

    [Test]
    public async Task Install_ABadUrl_Exits1_WithGitsError_AndLeavesNothing()
    {
        TestGit.Require();
        var nowhere = Path.Combine(_root, "nowhere");

        var (exitCode, output, error) = await RunInstall(Install(nowhere));

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(output).IsEqualTo("");
        await Assert.That(error).StartsWith($"Fetching {nowhere} failed: ");
        await Assert.That(error).Contains("does not appear to be a git repository");
        await Assert.That(Directory.GetFileSystemEntries(Registry)).IsEmpty();
    }

    [Test]
    public async Task Install_ABadRef_Exits1()
    {
        var repo = SkillRepo();

        var (exitCode, _, error) = await RunInstall(Install(repo, reference: "no-such-ref"));

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(error).StartsWith($"Fetching {repo} at no-such-ref failed: ");
    }

    [Test]
    [Arguments("../escape")]
    [Arguments(".hidden")]
    [Arguments("")]
    public async Task Install_ANameThatIsNotAFolderName_Exits2(string name)
    {
        var (exitCode, _, error) = await RunInstall(Install(Path.Combine(_root, "whatever"), name: name));

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error).Contains("The name must be a folder name");
    }

    [Test]
    public async Task Install_APathThatLeavesTheRepository_Exits2()
    {
        var repo = SkillRepo();

        var (exitCode, _, error) = await RunInstall(Install(repo, path: "../outside"));

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error).Contains("leaves the repository");
    }

    [Test]
    public async Task Install_UnreadableInput_Exits2()
    {
        var (exitCode, _, error) = await Run("not json", "skill-install");

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error).Contains("unreadable skill install");
    }

    [Test]
    public async Task Remove_RemovesTheFolder_AndIsFineWhenItsGone()
    {
        await RunInstall(Install(SkillRepo()));

        var first = await Run(null, "skill-remove", Registry, "pdf");
        var again = await Run(null, "skill-remove", Registry, "pdf");

        await Assert.That(first.ExitCode).IsEqualTo(0);
        await Assert.That(again.ExitCode).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(Registry, "pdf"))).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(Registry)).IsEmpty();
    }

    [Test]
    [Arguments("relative/root", "pdf")]
    [Arguments("/abs", "../x")]
    [Arguments("/abs", "")]
    public async Task Remove_BadArguments_Exits2(string root, string name)
    {
        var (exitCode, _, error) = await Run(null, "skill-remove", root, name);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error).Contains("skill-remove <root> <name>");
    }

    [Test]
    public async Task Launch_CopiesEachSkillIntoTheSessionsFolder_ThenStartsTheAgent()
    {
        var source = Path.Combine(_root, "registry", "pdf");
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), Pdf);
        await File.WriteAllTextAsync(Path.Combine(source, "scripts", "fill.py"), "print('fill')\n");
        File.CreateSymbolicLink(Path.Combine(source, "link.md"), "SKILL.md");
        var folder = Path.Combine(_root, "session");
        var target = Path.Combine(folder, "skills", "pdf");
        var kept = Path.Combine(folder, "skills", "kept");
        Directory.CreateDirectory(kept);
        await File.WriteAllTextAsync(Path.Combine(kept, "SKILL.md"), "the session's own copy");

        var exit = await LaunchAsync(folder,
        [
            new SupervisorSkill { Name = "pdf", Source = source, Target = target },
            new SupervisorSkill { Name = "kept", Source = source, Target = kept },
        ]);

        await Assert.That(exit.Error).IsNull();
        await Assert.That(exit.ExitCode).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Path.Combine(target, "SKILL.md"))).IsEqualTo(Pdf);
        await Assert.That(File.ReadAllText(Path.Combine(target, "scripts", "fill.py"))).IsEqualTo("print('fill')\n");
        await Assert.That(new FileInfo(Path.Combine(target, "link.md")).LinkTarget).IsEqualTo("SKILL.md");
        // One that's there already is the session's: left as it is.
        await Assert.That(File.ReadAllText(Path.Combine(kept, "SKILL.md"))).IsEqualTo("the session's own copy");
        await Assert.That(Entries(Path.Combine(folder, "skills"))).IsEquivalentTo(["pdf", "kept"]);
    }

    [Test]
    public async Task Launch_ASkillThatCantBeCopied_EndsTheRunWithoutAnAgent()
    {
        var folder = Path.Combine(_root, "session");
        var marker = Path.Combine(_root, "agent-ran");

        var exit = await LaunchAsync(folder,
            [new SupervisorSkill { Name = "pdf", Source = Path.Combine(_root, "registry", "gone"), Target = Path.Combine(folder, "skills", "pdf") }],
            agent: ["-c", $"touch {marker}"]);

        await Assert.That(exit.ExitCode).IsEqualTo(127);
        await Assert.That(exit.Error).StartsWith("Copying skill 'pdf' failed: ");
        await Assert.That(exit.Error).Contains("isn't a folder");
        await Assert.That(File.Exists(marker)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(folder, "skills"))).IsFalse();
    }

    /// <summary>Runs the supervisor for a launch with <paramref name="skills"/> and a short-lived agent (<c>sh</c>); its <c>exit.json</c>.</summary>
    private async Task<SupervisorExit> LaunchAsync(string folder, IReadOnlyList<SupervisorSkill> skills, string[]? agent = null)
    {
        Directory.CreateDirectory(folder);
        var launch = new SupervisorLaunch
        {
            Version = SupervisorProtocol.Version,
            RunId = Guid.NewGuid(),
            Command = "/bin/sh",
            Arguments = agent ?? ["-c", "exit 0"],
            Environment = new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin" },
            WorkingDirectory = Path.Combine(folder, "work"),
            Workplace = Path.Combine(folder, "work"),
            Skills = skills,
            Folder = folder,
            SocketPath = Path.Combine(_root, "sock", "run.sock"),
            ClientUid = Posix.GetEUid(),
        };
        using (var supervisor = Process.Start(new ProcessStartInfo(Supervisor) { RedirectStandardInput = true, UseShellExecute = false })!)
        {
            await supervisor.StandardInput.WriteAsync(JsonSerializer.Serialize(launch, SupervisorJsonContext.Default.SupervisorLaunch));
            supervisor.StandardInput.Close();
            // The supervisor detaches at once; the run goes on in its own session.
            using var timeout = new CancellationTokenSource(Guard);
            await supervisor.WaitForExitAsync(timeout.Token);
        }

        var deadline = DateTime.UtcNow + Guard;
        SupervisorExit? exit;
        while ((exit = SupervisorFiles.ReadExit(folder)) is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No exit.json in {folder} after {Guard.TotalSeconds:0} s.");
            }

            await Task.Delay(50);
        }

        return exit;
    }
}
