using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// One <see cref="FakeProvider"/> agent: waits out its <see cref="FakeScript"/>, or until killed,
/// telling <c>conversation</c> what it was told, its <see cref="FakeTurn"/> and how its turn ended.
/// </summary>
internal sealed class FakeRun : IAgentRun
{
    // Never disposed: Kill may come after the run ended, and a CTS without a timer holds nothing.
    private readonly CancellationTokenSource _kill = new();

    public FakeRun(FakeScript script, int attempt, TimeProvider time, string prompt, Action<IEnumerable<ConversationEntryRecord>> conversation)
    {
        var now = time.GetUtcNow();
        conversation([new ConversationEntryRecord(ConversationEntryKind.Prompt, Text: prompt, At: now)]);
        if (script.IsDownOn(attempt))
        {
            var refused = Refused(script, time);
            conversation([TurnEnd(refused, time)!]);
            Completion = Task.FromResult(refused);
            return;
        }

        conversation(FakeTurn.Parse(prompt, now));
        Completion = RunAsync(script, time).ContinueWith(
            run =>
            {
                if (TurnEnd(run.Result, time) is { } end)
                {
                    conversation([end]);
                }

                return run.Result;
            },
            TaskScheduler.Default);
    }

    public string ProviderSessionId { get; } = $"fake-{Guid.NewGuid():N}";

    public Task<AgentOutcome> Completion { get; }

    public void Kill() => _kill.Cancel();

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
