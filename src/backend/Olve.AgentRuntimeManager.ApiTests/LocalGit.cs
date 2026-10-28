using System.Diagnostics;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Local git repositories for the skill tests (M10b): no network, a plain path as the URL. Every
/// command is bounded, and commits carry their own identity (the test machine may have none).
/// </summary>
public static class LocalGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Skips the test where there's no git (the pipeline's check image has it).</summary>
    public static void Require() =>
        Skip.When(
            !(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, "git"))),
            "git isn't installed.");

    /// <summary>
    /// A new repository in a temp folder with one commit on <c>main</c> holding <paramref name="files"/>
    /// (path in the repository → content); its path, to use as the clone URL.
    /// </summary>
    public static string Repository(IReadOnlyDictionary<string, string> files)
    {
        Require();
        var path = Directory.CreateTempSubdirectory("arm-skill-repo-").FullName;
        Run(path, "init", "--quiet", "-b", "main");
        foreach (var (file, content) in files)
        {
            var full = Path.Combine(path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Run(path, "add", "--all");
        Run(path, "commit", "--quiet", "-m", "the skills");
        return path;
    }

    /// <summary>A <c>SKILL.md</c> in the open Agent Skills format.</summary>
    public static string SkillFile(string name, string description, string body = "Follow the steps.") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\n{body}\n";

    private static void Run(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["-c", "user.name=ARM tests", "-c", "user.email=tests@arm.invalid", "-c", "commit.gpgsign=false", .. arguments])
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', arguments)} took more than {Timeout.TotalSeconds:0} s.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {directory}: {error.Result.Trim()}");
        }

        _ = output.Result;
    }
}
