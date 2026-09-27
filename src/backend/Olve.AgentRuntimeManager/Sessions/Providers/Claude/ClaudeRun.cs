using System.Globalization;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// One Claude Code agent, run by its session's supervisor (which gave it its prompt, and keeps
/// every line it writes in <c>output.jsonl</c> and <c>stderr.log</c>): reads the turn, and ends
/// the agent after its first turn's result by closing its input. Re-attached after a restart, it
/// reads the run's output from the start and carries on the same way.
/// </summary>
internal sealed class ClaudeRun : IAgentRun
{
    private const int StderrTailLength = 2_000;

    private readonly SupervisedAgent _agent;
    private volatile bool _killed;
    private volatile bool _stoppedLingering;

    public ClaudeRun(SupervisedAgent agent, string providerSessionId, TimeSpan exitGrace)
    {
        _agent = agent;
        ProviderSessionId = providerSessionId;
        Completion = Task.Run(() => RunAsync(exitGrace));
    }

    public string ProviderSessionId { get; }

    public Task<AgentOutcome> Completion { get; }

    public void Kill()
    {
        // First, so an exit racing the kill still reads as killed.
        _killed = true;
        _agent.Kill();
    }

    private async Task<AgentOutcome> RunAsync(TimeSpan exitGrace)
    {
        try
        {
            ClaudeResult? result = null;
            var signals = new ClaudeSignals();
            Task? exitGuard = null;
            while (await _agent.ReadLineAsync() is { } line)
            {
                if (result is not null || ClaudeStreamJson.Parse(line) is not { } e)
                {
                    continue;
                }

                signals = signals.Read(e);
                if (ClaudeStreamJson.Result(e) is { } turnResult)
                {
                    result = turnResult;
                    // One prompt, one turn: no more input ends the agent.
                    _agent.CloseInput();
                    exitGuard = StopIfStillRunningAsync(exitGrace);
                }
            }

            var exitCode = await _agent.Exit;
            if (exitGuard is not null)
            {
                await exitGuard;
            }

            // An agent stopped only for lingering after a good turn still finished its work.
            exitCode = _stoppedLingering && result is { IsError: false } ? 0 : exitCode;
            return _killed ? new AgentOutcome.Killed() : Outcome(result, exitCode, _agent.StderrTail(StderrTailLength), signals);
        }
        catch (Exception exception) when (!_killed)
        {
            return new AgentOutcome.Failed($"Running Claude Code failed: {exception.Message}");
        }
        catch (Exception)
        {
            return new AgentOutcome.Killed();
        }
    }

    internal static AgentOutcome Outcome(ClaudeResult? result, int exitCode, string stderrTail, ClaudeSignals signals = default) => result switch
    {
        { IsError: false } => new AgentOutcome.Completed(exitCode),
        { TerminalReason: "api_error" } refused when !signals.ModelAnswered && Unavailable(refused, signals) is { } unavailable => unavailable,
        { } failed => new AgentOutcome.Failed(
            (failed.Subtype == "success" ? "Claude Code's turn failed" : $"Claude Code's turn ended with {failed.Subtype}")
            + (failed.Errors.Count > 0 ? $": {string.Join("; ", failed.Errors)}" : failed.Text is { Length: > 0 } text ? $": {text}" : ".")),
        null => new AgentOutcome.Failed(
            $"Claude Code exited with code {exitCode} before finishing its turn"
            + (stderrTail.Trim() is { Length: > 0 } tail ? $": {tail}" : ".")),
    };

    /// <summary>
    /// The API refused the turn before the model wrote anything: the provider's trouble, not the
    /// session's, when the status says so. Anything else (a 400, an unknown model's 404) is the
    /// session's own failure.
    /// </summary>
    private static AgentOutcome.Unavailable? Unavailable(ClaudeResult refused, ClaudeSignals signals)
    {
        var error = refused.Text is { Length: > 0 } text ? text : $"The API refused the turn ({refused.ApiErrorStatus?.ToString(CultureInfo.InvariantCulture) ?? "unreachable"}).";
        // Not logged in at all is no HTTP status (the request is never sent), but the same error kind.
        if (signals.ApiError == "authentication_failed")
        {
            return new AgentOutcome.Unavailable(ProviderTrouble.Unauthorized, error);
        }

        return refused.ApiErrorStatus switch
        {
            401 or 403 => new AgentOutcome.Unavailable(ProviderTrouble.Unauthorized, error),
            429 when signals.LimitReached => new AgentOutcome.Unavailable(ProviderTrouble.Limited, error, signals.LimitResetsAt),
            // Throttled without a usage limit: like an overloaded API.
            429 => new AgentOutcome.Unavailable(ProviderTrouble.Unreachable, error),
            // Not reached at all (network), or failing on its side (5xx, 529 overloaded).
            null or >= 500 => new AgentOutcome.Unavailable(ProviderTrouble.Unreachable, error),
            _ => null,
        };
    }

    /// <summary>Stops an agent that hasn't exited <paramref name="grace"/> after its input was closed.</summary>
    private async Task StopIfStillRunningAsync(TimeSpan grace)
    {
        try
        {
            await _agent.Exit.WaitAsync(grace);
        }
        catch (TimeoutException)
        {
            _stoppedLingering = true;
            _agent.Kill();
        }
        catch (Exception)
        {
            // The run's own loop reports a lost supervisor.
        }
    }
}
