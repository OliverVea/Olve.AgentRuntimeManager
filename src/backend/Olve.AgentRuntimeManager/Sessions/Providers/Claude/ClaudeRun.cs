using System.Globalization;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// One Claude Code agent, run by its session's supervisor (which gave it its first input, and keeps
/// every line it writes in <c>output.jsonl</c> and <c>stderr.log</c>): reads its turns, takes
/// messages while it works (M11), and ends the agent by closing its input once a turn has ended
/// with every message it was given taken up. Re-attached after a restart, it reads the run's
/// output from the start and carries on the same way.
/// </summary>
/// <remarks>
/// Claude Code works through its input one message after another, a turn (and a <c>result</c>)
/// each, and replays each message (<c>--replay-user-messages</c>, with the <c>uuid</c> it was given)
/// as it takes it up. The <c>result</c>'s <c>queued_turn_count</c> doesn't say whether more are
/// coming: it was 0 with a second message already waiting (recorded in the unit tests'
/// <c>Stub/two-messages.jsonl</c>). So the run counts: its input stays open while a message it
/// gave hasn't come back yet. Closing the input loses nothing already written: the agent still
/// works through it, then exits.
/// </remarks>
internal sealed class ClaudeRun : IAgentRun
{
    private const int StderrTailLength = 2_000;

    private readonly SupervisedAgent _agent;
    private readonly TimeSpan _exitGrace;
    private readonly Lock _gate = new();

    /// <summary>Messages given to the agent that it hasn't taken up (replayed) yet.</summary>
    private readonly HashSet<Guid> _unread;

    private bool _inputClosed;
    private CancellationTokenSource? _guard;
    private Task? _guarding;
    private volatile bool _killed;
    private volatile bool _stoppedLingering;

    /// <param name="given">
    /// The uuids of the messages in its first input after the first, which is taken up before any
    /// turn can end (none for a re-attached run: it can't know them).
    /// </param>
    public ClaudeRun(SupervisedAgent agent, string providerSessionId, TimeSpan exitGrace, IEnumerable<Guid>? given = null)
    {
        _agent = agent;
        _exitGrace = exitGrace;
        _unread = [.. given ?? []];
        ProviderSessionId = providerSessionId;
        Completion = Task.Run(RunAsync);
    }

    public string ProviderSessionId { get; }

    public Task<AgentOutcome> Completion { get; }

    public void Kill()
    {
        // First, so an exit racing the kill still reads as killed.
        _killed = true;
        _agent.Kill();
    }

    /// <summary>Writes the message to the agent's input; false once that's closed (or the agent is gone).</summary>
    public bool TrySend(string text)
    {
        lock (_gate)
        {
            if (_inputClosed || _killed || _agent.Exit.IsCompleted)
            {
                return false;
            }

            var id = Guid.NewGuid();
            _unread.Add(id);
            _agent.WriteLine(ClaudeStreamJson.UserMessage(text, id));
            return true;
        }
    }

    private async Task<AgentOutcome> RunAsync()
    {
        try
        {
            ClaudeResult? result = null;
            var signals = new ClaudeSignals();
            while (await _agent.ReadLineAsync() is { } line)
            {
                if (ClaudeStreamJson.Parse(line) is not { } e)
                {
                    continue;
                }

                if (ClaudeStreamJson.IsReplay(e, out var uuid))
                {
                    TurnStarted(uuid);
                    continue;
                }

                signals = signals.Read(e);
                if (ClaudeStreamJson.Result(e) is { } turnResult)
                {
                    // The session's outcome is its last turn's.
                    result = turnResult;
                    TurnEnded();
                }
            }

            var exitCode = await _agent.Exit;
            Task? guarding;
            lock (_gate)
            {
                _guard?.Cancel();
                guarding = _guarding;
            }

            if (guarding is not null)
            {
                await guarding;
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

    /// <summary>The agent took up a message: a turn is under way, so it isn't lingering.</summary>
    private void TurnStarted(Guid? uuid)
    {
        lock (_gate)
        {
            if (uuid is { } id)
            {
                _unread.Remove(id);
            }

            _guard?.Cancel();
        }
    }

    /// <summary>
    /// A turn ended: with every message taken up, no more input ends the agent. Either way it's
    /// watched: a message still unread that doesn't come up within the grace closes the input
    /// anyway (the agent works through what it has, then exits), and an agent that hasn't exited
    /// a grace after its input closed is stopped.
    /// </summary>
    private void TurnEnded()
    {
        lock (_gate)
        {
            if (_unread.Count == 0)
            {
                CloseInputLocked();
            }

            _guard?.Cancel();
            _guard = new CancellationTokenSource();
            _guarding = GuardAsync(_guard.Token);
        }
    }

    private void CloseInputLocked()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _agent.CloseInput();
        }
    }

    private async Task GuardAsync(CancellationToken turnStarted)
    {
        while (true)
        {
            try
            {
                await _agent.Exit.WaitAsync(_exitGrace, turnStarted);
                return;
            }
            catch (TimeoutException)
            {
                // Still running a grace later: see below.
            }
            catch (Exception)
            {
                // A new turn started, or the supervisor is lost (the run's own loop reports that).
                return;
            }

            lock (_gate)
            {
                if (turnStarted.IsCancellationRequested)
                {
                    return;
                }

                if (_inputClosed)
                {
                    _stoppedLingering = true;
                    _agent.Kill();
                    return;
                }

                // A message it was given never came up: no more input, then.
                CloseInputLocked();
            }
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
}
