using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// Runs git for a checkout (M5e), with the agent's environment (so its <c>HOME</c>, ssh key and
/// git identity apply) and never waiting on a person: no terminal prompt, ssh in batch mode unless
/// the session sets its own ssh command, no input, no hooks (one an agent planted in a shared copy
/// would run in another session's supervisor). Each command is bounded as a whole by
/// <c>timeout</c> (or ends when <c>cancel</c> fires): then it gets SIGTERM, SIGKILL a short grace
/// later, and what it wrote until then is returned.
/// </summary>
/// <remarks>
/// Its output is read only a moment past its exit: a process it left behind that still holds the
/// pipes (an ssh ControlMaster with ControlPersist) must not hold the checkout.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class Git(IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancel = default)
{
    /// <summary>How long git has between SIGTERM and SIGKILL, to remove its lock files.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long its output is read after it exited.</summary>
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(1);

    /// <summary>Where git says which repository to use: ours are always given with <c>-C</c>.</summary>
    private static readonly string[] Withheld = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_NAMESPACE"];

    private readonly string _command = Resolve(environment);

    public TimeSpan Timeout => timeout;

    public CancellationToken Cancel => cancel;

    /// <summary>The same git, bounded by <paramref name="otherTimeout"/> and not stopped by a kill (for cleaning up after one).</summary>
    public Git Uncancellable(TimeSpan otherTimeout) => new(environment, otherTimeout);

    public async Task<GitResult> RunAsync(params string[] arguments)
    {
        if (cancel.IsCancellationRequested)
        {
            return new GitResult(-1, "", "", TimedOut: false, Cancelled: true);
        }

        var info = new ProcessStartInfo(_command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["-c", "core.hooksPath=/dev/null", .. arguments])
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
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var stopReading = new CancellationTokenSource();
        var reading = Task.WhenAll(ReadAsync(process.StandardOutput, output, stopReading.Token), ReadAsync(process.StandardError, error, stopReading.Token));
        using var expired = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        expired.CancelAfter(timeout);
        var (timedOut, cancelled) = (false, false);
        try
        {
            await process.WaitForExitAsync(expired.Token);
        }
        catch (OperationCanceledException)
        {
            (timedOut, cancelled) = (!cancel.IsCancellationRequested, cancel.IsCancellationRequested);
            await ProcessTree.StopAsync(process.Id, () => process.HasExited, StopGrace);
            using var reaped = new CancellationTokenSource(StopGrace);
            try
            {
                await process.WaitForExitAsync(reaped.Token);
            }
            catch (OperationCanceledException)
            {
                // SIGKILLed: gone in a moment, whatever the wait says.
            }
        }

        await Task.WhenAny(reading, Task.Delay(ReadGrace));
        await stopReading.CancelAsync();
        return new GitResult(process.HasExited && !timedOut && !cancelled ? process.ExitCode : -1, Text(output), Text(error), timedOut, cancelled);
    }

    private static async Task ReadAsync(StreamReader reader, StringBuilder into, CancellationToken stop)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, stop)) > 0)
            {
                lock (into)
                {
                    into.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped reading (or the pipe went with the process): what was read is kept.
        }
    }

    private static string Text(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString().Trim();
        }
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
