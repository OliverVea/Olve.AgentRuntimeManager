using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// One <see cref="FakeProvider"/> agent: waits out its <see cref="FakeScript"/>, or until killed,
/// telling <c>conversation</c> what it was told (and later messages, M11), each with its
/// <see cref="FakeTurn"/>, and how its turn ended.
/// </summary>
internal sealed class FakeRun : IAgentRun
{
    // Never disposed: Kill may come after the run ended, and a CTS without a timer holds nothing.
    private readonly CancellationTokenSource _kill = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Action<IEnumerable<ConversationEntryRecord>> _conversation;
    private bool _ended;

    /// <param name="input">What it's told first, in order: the prompt and held messages.</param>
    /// <param name="resume">The provider session it resumes (a continuing session), if any.</param>
    public FakeRun(FakeScript script, int attempt, TimeProvider time, IReadOnlyList<string> input,
        Action<IEnumerable<ConversationEntryRecord>> conversation, string? resume = null)
    {
        _time = time;
        _conversation = conversation;
        ProviderSessionId = resume ?? $"fake-{Guid.NewGuid():N}";
        var now = time.GetUtcNow();
        if (script.IsDownOn(attempt))
        {
            var refused = Refused(script, time);
            conversation([.. input.Select(text => Told(text, now)), TurnEnd(refused, time)!]);
            _ended = true;
            Completion = Task.FromResult(refused);
            return;
        }

        conversation(input.SelectMany(text => (IEnumerable<ConversationEntryRecord>)[Told(text, now), .. FakeTurn.Parse(text, now)]));
        Completion = RunAsync(script, time).ContinueWith(
            run =>
            {
                lock (_gate)
                {
                    _ended = true;
                    if (TurnEnd(run.Result, time) is { } end)
                    {
                        conversation([end]);
                    }
                }

                return run.Result;
            },
            TaskScheduler.Default);
    }

    public string ProviderSessionId { get; }

    public Task<AgentOutcome> Completion { get; }

    public void Kill() => _kill.Cancel();

    /// <summary>Shows the message in its conversation, with the steps its directives script; false once it has ended.</summary>
    public bool TrySend(string text)
    {
        lock (_gate)
        {
            if (_ended || _kill.IsCancellationRequested)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            _conversation([Told(text, now), .. FakeTurn.Parse(text, now)]);
            return true;
        }
    }

    private static ConversationEntryRecord Told(string text, DateTimeOffset at) => new(ConversationEntryKind.Prompt, Text: text, At: at);

    private async Task<AgentOutcome> RunAsync(FakeScript script, TimeProvider time)
    {
        try
        {
            await Task.Delay(script.Hang ? Timeout.InfiniteTimeSpan : script.Delay, time, _kill.Token);
        }
        catch (OperationCanceledException)
        {
            return new AgentOutcome.Killed();
        }

        return script.Failure is { } failure
            ? new AgentOutcome.Failed(failure)
            : new AgentOutcome.Completed(script.ExitCode);
    }

    /// <summary>How the turn ended, for the conversation; a killed agent's turn just stops.</summary>
    private static ConversationEntryRecord? TurnEnd(AgentOutcome outcome, TimeProvider time) => outcome switch
    {
        AgentOutcome.Completed => new ConversationEntryRecord(ConversationEntryKind.TurnEnd, Text: "Done.", IsError: false, At: time.GetUtcNow()),
        AgentOutcome.Failed failed => new ConversationEntryRecord(ConversationEntryKind.TurnEnd, Text: failed.Error, IsError: true, At: time.GetUtcNow()),
        AgentOutcome.Unavailable refused => new ConversationEntryRecord(ConversationEntryKind.TurnEnd, Text: refused.Error, IsError: true, At: time.GetUtcNow()),
        _ => null,
    };

    private static AgentOutcome Refused(FakeScript script, TimeProvider time) => script.Down switch
    {
        ProviderTrouble.Limited => new AgentOutcome.Unavailable(ProviderTrouble.Limited, "Fake usage limit reached.", time.GetUtcNow() + script.Delay),
        ProviderTrouble.Unauthorized => new AgentOutcome.Unavailable(ProviderTrouble.Unauthorized, "Fake credentials rejected."),
        _ => new AgentOutcome.Unavailable(ProviderTrouble.Unreachable, "Fake API unreachable."),
    };
}
