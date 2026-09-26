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

    /// <summary>The server restarts: the old manager lets go (its agents are lost), a new one recovers.</summary>
    private ControlledProvider Restart()
    {
        _sessions.Dispose();
        var provider = new ControlledProvider();
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
        await Assert.That(_provider.RunOf(session.Id).Launch).IsEqualTo(new AgentLaunch(session.Id, "do it", "m"));
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

        _provider.RunOf(running.Id).End(new AgentOutcome.Completed(0, "done"));

        var completed = await Eventually(running.Id, s => s.Status == SessionStatus.Completed);
        await Assert.That(completed.ExitCode).IsEqualTo(0);
        await Assert.That(completed.Summary).IsEqualTo("done");
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
        _provider.RunOf(session.Id).End(new AgentOutcome.Completed(0, null));
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
        _provider.RunOf(done.Id).End(new AgentOutcome.Completed(0, "all done"));
        await Eventually(done.Id, s => s.Status == SessionStatus.Completed);

        Restart();

        await Assert.That(_sessions.Get(done.Id)).IsEqualTo(_sessions.Search(new SessionSearch(), 10, 0).Items.Single());
        await Assert.That(_sessions.Get(done.Id)!.Summary).IsEqualTo("all done");
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
