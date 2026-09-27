using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.UnitTests.Persistence;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

public class SessionManagerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ControlledProvider _provider = new();
    private readonly EventBus _bus;
    private readonly EventBus.Subscription _events;
    private readonly TestDatabase _database = new();
    private SessionManager _sessions;

    public SessionManagerTests()
    {
        _bus = new EventBus(_time, Options.Create(new EventOptions()));
        _events = _bus.Subscribe();
        _sessions = NewManager();
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _events.Dispose();
        _database.Dispose();
    }

    /// <summary>A manager on the test database; a second one is the server after a restart.</summary>
    private SessionManager NewManager(IAgentProvider? provider = null) => new(
        _bus,
        _time,
        Options.Create(new SessionOptions { TotalSlots = 2, MaxQueueSize = 2 }),
        [provider ?? _provider],
        _database.Store(),
        NullLogger<SessionManager>.Instance);

    /// <summary>
    /// The server restarts: the old manager lets go, a new one recovers. Its provider finds the
    /// working sessions' agents as <paramref name="recovering"/> says (unset: they're lost), after
    /// the server was down for <paramref name="away"/>.
    /// </summary>
    private ControlledProvider Restart(Func<ControlledProvider, AgentRecovery, RecoveredAgent?>? recovering = null, TimeSpan away = default)
    {
        _sessions.Dispose();
        _time.Advance(away);
        var provider = new ControlledProvider();
        if (recovering is not null)
        {
            provider.Recovering = r => recovering(provider, r);
        }

        _sessions = NewManager(provider);
        _sessions.Recover();
        return provider;
    }

    private CreateSession Request(string prompt = "do it", int? timeoutSeconds = null, string caller = "tests") =>
        new() { Prompt = prompt, Provider = _provider.Name, Model = "m", Caller = caller, TimeoutSeconds = timeoutSeconds };

    private SessionRecord Create(string prompt = "do it", int? timeoutSeconds = null, string caller = "tests") =>
        _sessions.Create(Request(prompt, timeoutSeconds, caller)) switch
        {
            CreateOutcome.Started s => s.Session,
            CreateOutcome.Queued q => q.Session,
            var other => throw new InvalidOperationException($"Unexpected {other}."),
        };

    private List<string> EventTypes(Guid sessionId)
    {
        var types = new List<string>();
        while (_events.Live.TryRead(out var stored))
        {
            if (SessionIdOf(stored.Data) == sessionId)
            {
                types.Add(stored.Data.EventType);
            }
        }

        return types;
    }

    private static Guid? SessionIdOf(ArmEvent data) => data switch
    {
        SessionCreated e => e.SessionId,
        SessionQueued e => e.SessionId,
        SessionStarted e => e.SessionId,
        SessionCompleted e => e.SessionId,
        SessionFailed e => e.SessionId,
        SessionKilled e => e.SessionId,
        SessionCancelled e => e.SessionId,
        _ => null,
    };

    /// <summary>Agents end asynchronously (<see cref="SessionManager"/> watches them off the lock).</summary>
    private async Task<SessionRecord> Eventually(Guid id, Func<SessionRecord, bool> condition)
    {
        using var timeout = new CancellationTokenSource(Guard);
        while (true)
        {
            var session = _sessions.Get(id)!;
            if (condition(session))
            {
                return session;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    [Test]
    public async Task Create_WithAFreeSlot_StartsTheAgent()
    {
        var outcome = _sessions.Create(Request());

        var started = await Assert.That(outcome).IsTypeOf<CreateOutcome.Started>();
        var session = started!.Session;
        await Assert.That(session.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(session.StartedAt).IsEqualTo(Start);
        await Assert.That(session.ProviderSessionId).IsEqualTo(_provider.RunOf(session.Id).ProviderSessionId);
        await Assert.That(EventTypes(session.Id)).IsEquivalentTo(["session.created", "session.started"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_KeepsTheRequest_WithNoTimeoutByDefault()
    {
        var session = Create();

        await Assert.That(session.Provider).IsEqualTo(_provider.Name);
        await Assert.That(session.Model).IsEqualTo("m");
        await Assert.That(session.Caller).IsEqualTo("tests");
        await Assert.That(session.TimeoutSeconds).IsNull();
        await Assert.That(_provider.RunOf(session.Id).Launch).IsEqualTo(new AgentLaunch(session.Id, "do it", "m", 1, session.Id, session.RunId!.Value));
    }

    [Test]
    public async Task Create_WithEverySlotBusy_Queues_InOrder()
    {
        Create();
        Create();

        var first = _sessions.Create(Request());
        var second = _sessions.Create(Request());

        var queued = (CreateOutcome.Queued)first;
        await Assert.That(queued.Session.Status).IsEqualTo(SessionStatus.Queued);
        await Assert.That(queued.Session.QueuePosition).IsEqualTo(1);
        await Assert.That(((CreateOutcome.Queued)second).Session.QueuePosition).IsEqualTo(2);
        await Assert.That(EventTypes(queued.Session.Id)).IsEquivalentTo(["session.created", "session.queued"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_WithAFullQueue_CreatesNothing()
    {
        for (var i = 0; i < 4; i++)
        {
            Create();
        }

        var outcome = _sessions.Create(Request());

        await Assert.That(outcome).IsEqualTo(new CreateOutcome.QueueFull(2));
        await Assert.That(_sessions.Search(new SessionSearch(), 100, 0).Total).IsEqualTo(4);
    }

    [Test]
    public async Task Create_WithAnUnknownProvider_CreatesNothing()
    {
        var outcome = _sessions.Create(Request() with { Provider = "nope" });

        var unknown = (CreateOutcome.UnknownProvider)outcome;
        await Assert.That(unknown.Provider).IsEqualTo("nope");
        await Assert.That(unknown.Known).IsEquivalentTo([_provider.Name]);
    }

    [Test]
    public async Task AgentThatCantStart_FailsTheSession()
    {
        _provider.StartFailure = new InvalidOperationException("no binary");

        var session = Create();

        await Assert.That(session.Status).IsEqualTo(SessionStatus.Failed);
        await Assert.That(session.Error).IsEqualTo("no binary");
        await Assert.That(EventTypes(session.Id)).IsEquivalentTo(["session.created", "session.failed"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task AgentCompleting_CompletesTheSession_AndStartsTheNextQueued()
    {
        var running = Create();
        Create();
        var queued = Create();

        _provider.RunOf(running.Id).End(new AgentOutcome.Completed(0));

        var completed = await Eventually(running.Id, s => s.Status == SessionStatus.Completed);
        await Assert.That(completed.ExitCode).IsEqualTo(0);
        await Assert.That(completed.EndedAt).IsEqualTo(Start);
        var next = await Eventually(queued.Id, s => s.Status == SessionStatus.Working);
        await Assert.That(next.QueuePosition).IsNull();
    }

    [Test]
    public async Task AgentFailing_FailsTheSession()
    {
        var session = Create();

        _provider.RunOf(session.Id).End(new AgentOutcome.Failed("crashed"));

        var failed = await Eventually(session.Id, s => s.Status == SessionStatus.Failed);
        await Assert.That(failed.Error).IsEqualTo("crashed");
    }

    [Test]
    public async Task Kill_AWorkingSession_KillsTheAgent()
    {
        var session = Create();

        var outcome = _sessions.Kill(session.Id, "enough", KillSource.User, "oliver");

        var killed = ((KillOutcome.Stopped)outcome).Session;
        await Assert.That(killed.Status).IsEqualTo(SessionStatus.Killed);
        await Assert.That(killed.KillReason).IsEqualTo("enough");
        await Assert.That(killed.KillSource).IsEqualTo(KillSource.User);
        await Assert.That(killed.KillCaller).IsEqualTo("oliver");
        await Assert.That(_provider.RunOf(session.Id).WasKilled).IsTrue();
        await Assert.That(EventTypes(session.Id)).IsEquivalentTo(
            ["session.created", "session.started", "session.killed"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Kill_AQueuedSession_CancelsIt_AndRemovesItFromTheQueue()
    {
        Create();
        Create();
        var first = Create();
        var second = Create();

        _sessions.Kill(first.Id, null, KillSource.User);

        await Assert.That(_sessions.Get(first.Id)!.Status).IsEqualTo(SessionStatus.Cancelled);
        await Assert.That(_sessions.Get(first.Id)!.QueuePosition).IsNull();
        await Assert.That(_sessions.Get(second.Id)!.QueuePosition).IsEqualTo(1);
        await Assert.That(_provider.Runs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Kill_ARunningSession_StartsTheNextQueued()
    {
        var running = Create();
        Create();
        var queued = Create();

        _sessions.Kill(running.Id, null, KillSource.User);

        await Assert.That(_sessions.Get(queued.Id)!.Status).IsEqualTo(SessionStatus.Working);
    }

    [Test]
    public async Task Kill_AnEndedSession_IsAlreadyEnded_AndChangesNothing()
    {
        var session = Create();
        _sessions.Kill(session.Id, "first", KillSource.User);

        var outcome = _sessions.Kill(session.Id, "second", KillSource.User);

        await Assert.That(outcome).IsTypeOf<KillOutcome.AlreadyEnded>();
        await Assert.That(_sessions.Get(session.Id)!.KillReason).IsEqualTo("first");
    }

    [Test]
    public async Task Kill_AnUnknownSession_IsNotFound() =>
        await Assert.That(_sessions.Kill(Guid.NewGuid(), null, KillSource.User)).IsTypeOf<KillOutcome.NotFound>();

    [Test]
    public async Task Timeout_KillsTheSession()
    {
        var session = Create(timeoutSeconds: 5);

        _time.Advance(TimeSpan.FromSeconds(5));

        var killed = await Eventually(session.Id, s => s.Status == SessionStatus.Killed);
        await Assert.That(killed.KillSource).IsEqualTo(KillSource.Timeout);
        await Assert.That(killed.KillReason).IsEqualTo("Timed out after 5s.");
        await Assert.That(_provider.RunOf(session.Id).WasKilled).IsTrue();
    }

    [Test]
    public async Task WithoutATimeout_ASessionRunsUntilItEnds()
    {
        var session = Create();

        _time.Advance(TimeSpan.FromDays(2));

        await Assert.That(_sessions.Get(session.Id)!.Status).IsEqualTo(SessionStatus.Working);
    }

    [Test]
    public async Task Timeout_OfACompletedSession_DoesNothing()
    {
        var session = Create(timeoutSeconds: 5);
        _provider.RunOf(session.Id).End(new AgentOutcome.Completed(0));
        await Eventually(session.Id, s => s.Status == SessionStatus.Completed);

        _time.Advance(TimeSpan.FromSeconds(10));

        await Assert.That(_sessions.Get(session.Id)!.Status).IsEqualTo(SessionStatus.Completed);
    }

    [Test]
    public async Task AgentStoppedFromOutside_KillsTheSessionAsSystem()
    {
        var session = Create();

        _provider.RunOf(session.Id).End(new AgentOutcome.Killed());

        var killed = await Eventually(session.Id, s => s.Status == SessionStatus.Killed);
        await Assert.That(killed.KillSource).IsEqualTo(KillSource.System);
    }

    [Test]
    public async Task Delete_OnlyRemovesEndedSessions()
    {
        var session = Create();

        await Assert.That(_sessions.Delete(session.Id)).IsTypeOf<DeleteOutcome.NotEnded>();

        _sessions.Kill(session.Id, null, KillSource.User);
        await Assert.That(_sessions.Delete(session.Id)).IsTypeOf<DeleteOutcome.Deleted>();
        await Assert.That(_sessions.Get(session.Id)).IsNull();
        await Assert.That(_sessions.Delete(session.Id)).IsTypeOf<DeleteOutcome.NotFound>();
    }

    [Test]
    public async Task Restart_KeepsEndedSessions()
    {
        var done = Create("finish");
        _provider.RunOf(done.Id).End(new AgentOutcome.Completed(4));
        await Eventually(done.Id, s => s.Status == SessionStatus.Completed);

        Restart();

        await Assert.That(_sessions.Get(done.Id)).IsEqualTo(_sessions.Search(new SessionSearch(), 10, 0).Items.Single());
        await Assert.That(_sessions.Get(done.Id)!.ExitCode).IsEqualTo(4);
    }

    [Test]
    public async Task Restart_KillsWorkingSessions_AsSystem()
    {
        var working = Create();

        Restart();

        var killed = _sessions.Get(working.Id)!;
        await Assert.That(killed.Status).IsEqualTo(SessionStatus.Killed);
        await Assert.That(killed.KillSource).IsEqualTo(KillSource.System);
        await Assert.That(killed.KillReason).IsEqualTo("ARM restarted; the agent was lost.");
        await Assert.That(EventTypes(working.Id)).Contains("session.killed");
    }

    [Test]
    public async Task Restart_ReattachedSessions_KeepTheirSlots_AndFinishLater()
    {
        var first = Create();
        var second = Create();
        var queued = Create("queued");

        var provider = Restart((p, r) => p.Reattach(r));

        await Assert.That(provider.Recoveries.Select(r => r.SessionId)).IsEquivalentTo([first.Id, second.Id]);
        await Assert.That(provider.Recoveries[0].RunId).IsEqualTo(first.RunId!.Value);
        await Assert.That(_sessions.Get(first.Id)!.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(_sessions.Get(queued.Id)!.Status).IsEqualTo(SessionStatus.Queued);
        await Assert.That(provider.Runs.Count).IsEqualTo(2);

        provider.RunOf(first.Id).End(new AgentOutcome.Completed(0));

        await Eventually(first.Id, s => s.Status == SessionStatus.Completed);
        await Eventually(queued.Id, s => s.Status == SessionStatus.Working);
        await Assert.That(_sessions.Get(first.Id)!.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Restart_ResumedSession_RecordsItsNewRun_ButNoNewAttempt()
    {
        var working = Create();

        var provider = Restart((p, r) => p.Reattach(r, resumed: true));

        var resumed = _sessions.Get(working.Id)!;
        await Assert.That(resumed.RunId).IsEqualTo(provider.Recoveries.Single().ResumeRunId);
        await Assert.That(resumed.Attempts).IsEqualTo(1);
        await Assert.That(resumed.Status).IsEqualTo(SessionStatus.Working);
    }

    [Test]
    public async Task Restart_ReattachedSession_KeepsWhatsLeftOfItsTimeout()
    {
        var working = Create(timeoutSeconds: 60);

        Restart((p, r) => p.Reattach(r), away: TimeSpan.FromSeconds(50));
        _time.Advance(TimeSpan.FromSeconds(9));

        await Assert.That(_sessions.Get(working.Id)!.Status).IsEqualTo(SessionStatus.Working);

        _time.Advance(TimeSpan.FromSeconds(1));

        await Eventually(working.Id, s => s.Status == SessionStatus.Killed);
        await Assert.That(_sessions.Get(working.Id)!.KillSource).IsEqualTo(KillSource.Timeout);
    }

    [Test]
    public async Task Restart_TimeoutRanOutWhileAway_KillsTheReattachedSession()
    {
        var working = Create(timeoutSeconds: 60);

        var provider = Restart((p, r) => p.Reattach(r), away: TimeSpan.FromSeconds(61));

        await Eventually(working.Id, s => s.Status == SessionStatus.Killed);
        await Assert.That(_sessions.Get(working.Id)!.KillSource).IsEqualTo(KillSource.Timeout);
        await Assert.That(provider.RunOf(working.Id).WasKilled).IsTrue();
    }

    [Test]
    public async Task Restart_ProviderFailingToRecover_KillsTheSession_AsSystem()
    {
        var working = Create();

        Restart((_, _) => throw new InvalidOperationException("boom"));

        await Assert.That(_sessions.Get(working.Id)!.KillReason).IsEqualTo("ARM restarted; the agent was lost.");
    }

    [Test]
    public async Task Restart_QueuesQueuedSessionsAgain_InOrder_AndStartsThem()
    {
        Create();
        Create();
        var first = Create("first");
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = Create("second");

        var provider = Restart();

        // The two working ones were killed, freeing both slots for the queue, oldest first.
        await Assert.That(provider.Runs.Select(r => r.Launch.Prompt)).IsEquivalentTo(["first", "second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(_sessions.Get(first.Id)!.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(_sessions.Get(second.Id)!.Status).IsEqualTo(SessionStatus.Working);
    }

    [Test]
    public async Task Restart_KeepsTheQueueOrder_WhileSlotsAreBusy()
    {
        using var database = new TestDatabase();
        var oneSlot = Options.Create(new SessionOptions { TotalSlots = 1, MaxQueueSize = 5 });
        var before = new SessionManager(_bus, _time, oneSlot, [_provider], database.Store(), NullLogger<SessionManager>.Instance);
        Record(before.Create(Request("working")));
        var a = Record(before.Create(Request("a")));
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = Record(before.Create(Request("b")));
        _time.Advance(TimeSpan.FromSeconds(1));
        var c = Record(before.Create(Request("c")));
        before.Dispose();

        var provider = new ControlledProvider();
        using var after = new SessionManager(_bus, _time, oneSlot, [provider], database.Store(), NullLogger<SessionManager>.Instance);
        after.Recover();

        // "working" was killed, so "a" starts; "b" and "c" wait in order.
        await Assert.That(after.Get(a.Id)!.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(after.Get(b.Id)!.QueuePosition).IsEqualTo(1);
        await Assert.That(after.Get(c.Id)!.QueuePosition).IsEqualTo(2);
        var queued = after.Search(new SessionSearch { Status = [SessionStatus.Queued] }, 10, 0).Items;
        await Assert.That(queued.Select(s => s.QueuePosition ?? 0).ToList()).IsEquivalentTo([2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Restart_ADeletedSessionStaysDeleted()
    {
        var session = Create();
        _sessions.Kill(session.Id, null, KillSource.User, "tests");
        await Assert.That(_sessions.Delete(session.Id)).IsTypeOf<DeleteOutcome.Deleted>();

        Restart();

        await Assert.That(_sessions.Get(session.Id)).IsNull();
        await Assert.That(_sessions.Delete(session.Id)).IsTypeOf<DeleteOutcome.NotFound>();
    }

    [Test]
    public async Task Send_ToAWorkingSession_DeliversItToTheAgent()
    {
        var working = Create();

        var outcome = _sessions.Send(working.Id, "also this", "tests");

        await Assert.That(outcome).IsTypeOf<MessageOutcome.Delivered>();
        await Assert.That(_provider.RunOf(working.Id).Sent).IsEquivalentTo(["also this"]);
        await Assert.That(_sessions.Get(working.Id)!.Messages).IsNull();
    }

    [Test]
    public async Task Send_ToAQueuedSession_HoldsIt_UntilItsAgentStarts_AfterThePrompt_InOrder()
    {
        var first = Create();
        Create();
        var queued = Create("the prompt");

        var outcomes = new[] { _sessions.Send(queued.Id, "one", "tests"), _sessions.Send(queued.Id, "two", "tests") };

        await Assert.That(outcomes.All(o => o is MessageOutcome.Pending)).IsTrue();
        await Assert.That(_sessions.Get(queued.Id)!.Messages).IsEquivalentTo(["one", "two"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        _provider.RunOf(first.Id).End(new AgentOutcome.Completed(0));

        var started = await Eventually(queued.Id, s => s.Status == SessionStatus.Working);
        var launch = _provider.RunOf(queued.Id).Launch;
        await Assert.That(launch.Input).IsEquivalentTo(["the prompt", "one", "two"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(launch.Resume).IsNull();
        await Assert.That(started.Messages).IsNull();
    }

    [Test]
    public async Task Send_ToAnEndedSession_ContinuesIt_ResumingItsAgent_InANewRun()
    {
        var session = Create();
        var firstRun = _provider.RunOf(session.Id);
        firstRun.End(new AgentOutcome.Completed(3));
        var ended = await Eventually(session.Id, s => s.Status == SessionStatus.Completed);
        EventTypes(session.Id);
        _time.Advance(TimeSpan.FromMinutes(5));

        var outcome = _sessions.Send(session.Id, "one more thing", "tests");

        await Assert.That(outcome).IsTypeOf<MessageOutcome.Continued>();
        var continued = _sessions.Get(session.Id)!;
        await Assert.That(continued.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(continued.RunId).IsNotEqualTo(ended.RunId);
        await Assert.That(continued.Attempts).IsEqualTo(2);
        await Assert.That(continued.StartedAt).IsEqualTo(Start.AddMinutes(5));
        await Assert.That(continued.EndedAt).IsNull();
        await Assert.That(continued.ExitCode).IsNull();
        await Assert.That(continued.ProviderSessionId).IsEqualTo(ended.ProviderSessionId);
        var launch = _provider.Runs.Last().Launch;
        await Assert.That(launch.Resume).IsEqualTo(ended.ProviderSessionId);
        await Assert.That(launch.Input).IsEquivalentTo(["one more thing"]);
        await Assert.That(EventTypes(session.Id)).IsEquivalentTo(["session.queued", "session.started"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Send_ToAnEndedSession_QueuesIt_AtTheEnd_WithTheStateItLeft()
    {
        var killed = Create();
        _sessions.Kill(killed.Id, "enough", KillSource.User, "tests");
        Create();
        Create();
        var waiting = Create();
        while (_events.Live.TryRead(out _))
        {
        }

        var outcome = _sessions.Send(killed.Id, "go on", "tests");

        await Assert.That(outcome).IsTypeOf<MessageOutcome.Continued>();
        var queued = _sessions.Get(killed.Id)!;
        await Assert.That(queued.Status).IsEqualTo(SessionStatus.Queued);
        await Assert.That(queued.QueuePosition).IsEqualTo(2);
        await Assert.That(_sessions.Get(waiting.Id)!.QueuePosition).IsEqualTo(1);
        // Its old outcome is gone.
        await Assert.That(queued.EndedAt).IsNull();
        await Assert.That(queued.KillReason).IsNull();
        await Assert.That(queued.KillSource).IsNull();
        await Assert.That(queued.KillCaller).IsNull();
        await Assert.That(queued.Messages).IsEquivalentTo(["go on"]);
        _events.Live.TryRead(out var stored);
        var e = await Assert.That(stored!.Data).IsTypeOf<SessionQueued>();
        await Assert.That(e!.Previous).IsEqualTo(SessionStatus.Killed);
        await Assert.That(e.Position).IsEqualTo(2);
    }

    [Test]
    public async Task Send_ToASessionCancelledBeforeItStarted_IsNeverStarted()
    {
        Create();
        Create();
        var queued = Create();
        _sessions.Kill(queued.Id, null, KillSource.User, "tests");

        var outcome = _sessions.Send(queued.Id, "hello?", "tests");

        await Assert.That(outcome).IsTypeOf<MessageOutcome.NeverStarted>();
        await Assert.That(_sessions.Get(queued.Id)!.Status).IsEqualTo(SessionStatus.Cancelled);
    }

    [Test]
    public async Task Send_ToAnUnknownSession_IsNotFound() =>
        await Assert.That(_sessions.Send(Guid.NewGuid(), "hello?", "tests")).IsTypeOf<MessageOutcome.NotFound>();

    [Test]
    public async Task Send_AsTheAgentsTurnEnds_IsHeld_AndTheSessionContinuesWithIt_OnceTheRunEnds()
    {
        var session = Create();
        var run = _provider.RunOf(session.Id);
        // Its turn just ended: it takes no more input.
        run.TakesMessages = false;

        var first = _sessions.Send(session.Id, "one", "tests");
        run.TakesMessages = true;
        // Held ones go first: the next mustn't overtake them.
        var second = _sessions.Send(session.Id, "two", "tests");

        await Assert.That(first).IsTypeOf<MessageOutcome.Held>();
        await Assert.That(second).IsTypeOf<MessageOutcome.Held>();
        await Assert.That(run.Sent).IsEmpty();
        await Assert.That(_sessions.Get(session.Id)!.Messages).IsEquivalentTo(["one", "two"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        run.End(new AgentOutcome.Completed(0));

        await Eventually(session.Id, s => s is { Status: SessionStatus.Working, Attempts: 2 });
        await Assert.That(EventTypes(session.Id)).IsEquivalentTo(
            ["session.created", "session.started", "session.completed", "session.queued", "session.started"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var launch = _provider.Runs.Last().Launch;
        await Assert.That(launch.Resume).IsEqualTo(run.ProviderSessionId);
        await Assert.That(launch.Input).IsEquivalentTo(["one", "two"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Kill_OfASessionHoldingMessages_DoesNotContinueIt()
    {
        var session = Create();
        _provider.RunOf(session.Id).TakesMessages = false;
        _sessions.Send(session.Id, "one", "tests");

        _sessions.Kill(session.Id, null, KillSource.User, "tests");
        await Task.Delay(50);

        var killed = _sessions.Get(session.Id)!;
        await Assert.That(killed.Status).IsEqualTo(SessionStatus.Killed);
        await Assert.That(killed.Messages).IsEquivalentTo(["one"]);
    }

    [Test]
    public async Task Continued_GetsAFreshTimeout()
    {
        var session = Create(timeoutSeconds: 60);
        _time.Advance(TimeSpan.FromSeconds(50));
        _provider.RunOf(session.Id).End(new AgentOutcome.Failed("boom"));
        var failed = await Eventually(session.Id, s => s.Status == SessionStatus.Failed);

        _sessions.Send(session.Id, "try again", "tests");
        _time.Advance(TimeSpan.FromSeconds(59));

        var continued = _sessions.Get(session.Id)!;
        await Assert.That(continued.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(continued.Error).IsNull();
        await Assert.That(failed.Error).IsEqualTo("boom");

        _time.Advance(TimeSpan.FromSeconds(1));

        var killed = await Eventually(session.Id, s => s.Status == SessionStatus.Killed);
        await Assert.That(killed.KillSource).IsEqualTo(KillSource.Timeout);
        await Assert.That(_provider.Runs.Last().WasKilled).IsTrue();
    }

    [Test]
    public async Task ProviderRefusingTheAgent_GivesItsMessagesBack_ForTheRetry()
    {
        var session = Create();
        _provider.RunOf(session.Id).End(new AgentOutcome.Completed(0));
        await Eventually(session.Id, s => s.Status == SessionStatus.Completed);
        _sessions.Send(session.Id, "go on", "tests");
        var continued = _provider.Runs.Last();

        continued.End(new AgentOutcome.Unavailable(ProviderTrouble.Unreachable, "down"));

        // The provider is paused: the session waits for its retry, still holding the message.
        var retrying = await Eventually(session.Id, s => s.Status == SessionStatus.Queued);
        await Assert.That(retrying.Messages).IsEquivalentTo(["go on"]);
        await Assert.That(retrying.FailedAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task Restart_KeepsHeldMessages_AndAContinuedSessionResumesLikeAnyOther()
    {
        var ended = Create();
        _provider.RunOf(ended.Id).End(new AgentOutcome.Completed(0));
        var completed = await Eventually(ended.Id, s => s.Status == SessionStatus.Completed);
        Create();
        Create();
        var queued = Create("the prompt");
        _sessions.Send(queued.Id, "held", "tests");
        _time.Advance(TimeSpan.FromSeconds(1));
        // Both slots are busy: it queues behind the one already waiting.
        _sessions.Send(ended.Id, "continue", "tests");

        var provider = Restart();

        // The two working ones were lost, freeing both slots for the queue, in its order.
        var launches = provider.Runs.Select(r => r.Launch).ToList();
        await Assert.That(launches.Select(l => l.SessionId).ToList()).IsEquivalentTo([queued.Id, ended.Id], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(launches[0].Input).IsEquivalentTo(["the prompt", "held"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(launches[1].Resume).IsEqualTo(completed.ProviderSessionId);
        await Assert.That(launches[1].Input).IsEquivalentTo(["continue"]);
    }

    private static SessionRecord Record(CreateOutcome outcome) => outcome switch
    {
        CreateOutcome.Started s => s.Session,
        CreateOutcome.Queued q => q.Session,
        var other => throw new InvalidOperationException($"Unexpected {other}."),
    };

    [Test]
    public async Task Search_FiltersAndPages_NewestFirst()
    {
        var old = Create(caller: "oribot");
        _time.Advance(TimeSpan.FromSeconds(1));
        var newer = Create(caller: "oribot");
        _time.Advance(TimeSpan.FromSeconds(1));
        Create(caller: "someone");

        var page = _sessions.Search(new SessionSearch { Caller = "oribot" }, limit: 1, offset: 0);
        var second = _sessions.Search(new SessionSearch { Caller = "oribot" }, limit: 1, offset: 1);

        await Assert.That(page.Total).IsEqualTo(2);
        await Assert.That(page.Items.Single().Id).IsEqualTo(newer.Id);
        await Assert.That(second.Items.Single().Id).IsEqualTo(old.Id);
        var byTime = _sessions.Search(new SessionSearch { CreatedAfter = Start, CreatedBefore = Start.AddSeconds(2) }, 10, 0);
        await Assert.That(byTime.Total).IsEqualTo(1);
        var byStatus = _sessions.Search(new SessionSearch { Status = [SessionStatus.Queued] }, 10, 0);
        await Assert.That(byStatus.Total).IsEqualTo(1);
        var byStatuses = _sessions.Search(new SessionSearch { Status = [SessionStatus.Queued, SessionStatus.Working] }, 10, 0);
        await Assert.That(byStatuses.Total).IsEqualTo(3);
    }
}
