using System.Diagnostics;
using System.Text;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// One Claude Code agent: sends the prompt, keeps every line the agent writes (<c>output.jsonl</c>,
/// <c>stderr.log</c> in the session's folder), and ends the agent after its first turn's result
/// by closing its input.
/// </summary>
internal sealed class ClaudeRun : IAgentRun
{
    private const int StderrTailLength = 2_000;

    private readonly Process _process;
    private volatile bool _killed;
    private volatile bool _stoppedLingering;

    public ClaudeRun(Process process, Guid sessionId, string prompt, string folder, TimeSpan exitGrace)
    {
        _process = process;
        ProviderSessionId = sessionId.ToString();
        // Off the caller's thread: providers start agents under the session runtime's lock.
        Completion = Task.Run(() => RunAsync(prompt, folder, exitGrace));
    }

    public string ProviderSessionId { get; }

    public Task<AgentOutcome> Completion { get; }

    public void Kill()
    {
        // First, so an exit racing the kill still reads as killed.
        _killed = true;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private async Task<AgentOutcome> RunAsync(string prompt, string folder, TimeSpan exitGrace)
    {
        try
        {
            var stderr = CollectStderrAsync(Path.Combine(folder, "stderr.log"));
            ClaudeResult? result = null;
            try
            {
                await _process.StandardInput.WriteLineAsync(ClaudeStreamJson.UserMessage(prompt));
                await _process.StandardInput.FlushAsync();
            }
            catch (IOException)
            {
                // The agent is already gone; its exit code and stderr tell why.
            }

            Task? exitGuard = null;
            await using (var output = new StreamWriter(Path.Combine(folder, "output.jsonl"), append: true))
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                {
                    await output.WriteLineAsync(line);
                    if (result is null && ClaudeStreamJson.ParseResult(line) is { } turnResult)
                    {
                        result = turnResult;
                        await output.FlushAsync();
                        // One prompt, one turn: no more input ends the agent.
                        CloseInput();
                        exitGuard = StopIfStillRunningAsync(exitGrace);
                    }
                }
            }

            // Output closed without a result, or the agent lingers: it gets the same grace to exit.
            exitGuard ??= StopIfStillRunningAsync(exitGrace);
            await _process.WaitForExitAsync();
            await exitGuard;

            var stderrTail = await stderr;
            // An agent stopped only for lingering after a good turn still finished its work.
            var exitCode = _stoppedLingering && result is { IsError: false } ? 0 : _process.ExitCode;
            return _killed ? new AgentOutcome.Killed() : Outcome(result, exitCode, stderrTail);
        }
        catch (Exception exception) when (!_killed)
        {
            return new AgentOutcome.Failed($"Running Claude Code failed: {exception.Message}");
        }
        catch (Exception)
        {
            return new AgentOutcome.Killed();
        }
        finally
        {
            _process.Dispose();
        }
    }

    internal static AgentOutcome Outcome(ClaudeResult? result, int exitCode, string stderrTail) => result switch
    {
        { IsError: false } => new AgentOutcome.Completed(exitCode),
        { } failed => new AgentOutcome.Failed(
            $"Claude Code's turn ended with {failed.Subtype}"
            + (failed.Errors.Count > 0 ? $": {string.Join("; ", failed.Errors)}" : failed.Text is { Length: > 0 } text ? $": {text}" : ".")),
        null => new AgentOutcome.Failed(
            $"Claude Code exited with code {exitCode} before finishing its turn"
            + (stderrTail.Trim() is { Length: > 0 } tail ? $": {tail}" : ".")),
    };

    /// <summary>Stops an agent that hasn't exited <paramref name="grace"/> after its input was closed.</summary>
    private async Task StopIfStillRunningAsync(TimeSpan grace)
    {
        using var timeout = new CancellationTokenSource(grace);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _stoppedLingering = true;
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited meanwhile.
            }
        }
    }

    private void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The agent already closed its end.
        }
    }

    /// <summary>Copies stderr to its file as it comes (so a full pipe never stalls the agent); returns its tail.</summary>
    private async Task<string> CollectStderrAsync(string path)
    {
        var tail = new StringBuilder();
        await using var file = new StreamWriter(path, append: true);
        var buffer = new char[4096];
        int read;
        while ((read = await _process.StandardError.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            tail.Append(buffer, 0, read);
            if (tail.Length > StderrTailLength)
            {
                tail.Remove(0, tail.Length - StderrTailLength);
            }
        }

        return tail.ToString();
    }
}
