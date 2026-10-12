using System.Diagnostics;
using System.Runtime.Versioning;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Runs git for a checkout (M5e), with the agent's environment (so its <c>HOME</c>, ssh key and
/// git identity apply) and never waiting on a person: no terminal prompt, ssh in batch mode unless
/// the session sets its own ssh command, no input, and every command killed after <c>timeout</c>.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class Git(IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
{
    /// <summary>Where git says which repository to use: ours are always given with <c>-C</c>.</summary>
    private static readonly string[] Withheld = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_NAMESPACE"];

    private readonly string _command = Resolve(environment);

    public TimeSpan Timeout => timeout;

    public async Task<GitResult> RunAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo(_command)
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

        info.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            if (!Withheld.Contains(name, StringComparer.Ordinal))
            {
                info.Environment[name] = value;
            }
        }

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        if (!environment.ContainsKey("GIT_SSH_COMMAND") && !environment.ContainsKey("GIT_SSH"))
        {
            info.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"'{_command}' did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var expired = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(expired.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Ended just now.
            }

            return new GitResult(-1, "", "", TimedOut: true);
        }

        return new GitResult(process.ExitCode, (await output).Trim(), (await error).Trim(), TimedOut: false);
    }

    /// <summary>git on the agent's <c>PATH</c> (the supervisor's own environment is empty), else the usual places.</summary>
    private static string Resolve(IReadOnlyDictionary<string, string> environment)
    {
        var path = environment.TryGetValue("PATH", out var value) ? value : "";
        foreach (var directory in $"{path}:/usr/local/bin:/usr/bin:/bin".Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(directory, "git");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "git";
    }
}
