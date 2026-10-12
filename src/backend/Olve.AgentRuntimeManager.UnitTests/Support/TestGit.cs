using System.Diagnostics;

namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>
/// Local git repositories for the checkout tests (M5e): no network, plain paths as URLs. Every
/// command is bounded, and commits carry their own identity (the test machine may have none).
/// </summary>
public static class TestGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs git in <paramref name="directory"/>; its output, trimmed. Throws if it fails.</summary>
    public static string Run(string directory, params string[] arguments)
    {
        var (exitCode, output, error) = TryRun(directory, arguments);
        return exitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {directory}: {error}");
    }

    /// <summary>Runs git in <paramref name="directory"/>: its exit code and output.</summary>
    public static (int ExitCode, string Output, string Error) TryRun(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["-c", "user.name=ARM tests", "-c", "user.email=tests@arm.invalid", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", .. arguments])
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

        return (process.ExitCode, output.Result.Trim(), error.Result.Trim());
    }

    /// <summary>Skips the test where there's no git (the pipeline's check image has it).</summary>
    public static void Require() =>
        Skip.When(
            !(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, "git"))),
            "git isn't installed.");

    /// <summary>A new repository at <paramref name="path"/> with one commit on <c>main</c>; its path.</summary>
    public static string Origin(string path)
    {
        Require();
        Directory.CreateDirectory(path);
        Run(path, "init", "--quiet", "-b", "main");
        Commit(path, "README.md", $"# {Path.GetFileName(path)}\n");
        return path;
    }

    /// <summary>Writes <paramref name="file"/> and commits it on the current branch; the commit's id.</summary>
    public static string Commit(string repository, string file, string content)
    {
        File.WriteAllText(Path.Combine(repository, file), content);
        Run(repository, "add", file);
        Run(repository, "commit", "--quiet", "-m", $"{file}: {content.Trim()}");
        return Head(repository);
    }

    public static string Head(string repository) => Run(repository, "rev-parse", "HEAD");
}
