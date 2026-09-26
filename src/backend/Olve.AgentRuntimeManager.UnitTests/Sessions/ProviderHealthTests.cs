using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.UnitTests.Persistence;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>
/// A provider refusing agents (<see cref="AgentOutcome.Unavailable"/>): it pauses, its sessions
/// wait and retry, other providers' sessions go on.
/// </summary>
public class ProviderHealthTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ControlledProvider _provider = new();
    private readonly ControlledProvider _other = new("other");
    private readonly EventBus _bus;
    private readonly EventBus.Subscription _events;
    private readonly TestDatabase _database = new();
    private readonly SessionManager _sessions;

    public ProviderHealthTests()
    {
        _bus = new EventBus(_time, Options.Create(new EventOptions()));
        _events = _bus.Subscribe();
        _sessions = new SessionManager(
            _bus,
            _time,
            Options.Create(new SessionOptions
            {
                TotalSlots = 2, MaxQueueSize = 10, ProviderRetries = 2, ProviderBackoff = Backoff, ProviderMaxBackoff = TimeSpan.FromMinutes(3),
            }),
            [_provider, _other],
            _database.Store(),
            NullLogger<SessionManager>.Instance);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _events.Dispose();
        _database.Dispose();
    }

    private static AgentOutcome.Unavailable Down(ProviderTrouble trouble = ProviderTrouble.Unreachable, DateTimeOffset? until = null) =>
        new(trouble, $"{trouble} for a while.", until);

    private SessionRecord Create(ControlledProvider? provider = null) =>
        _sessions.Create(new CreateSession { Prompt = "do it", Provider = (provider ?? _provider).Name, Model = "m", Caller = "tests" }) switch
        {
            CreateOutcome.Started s => s.Session,
            CreateOutcome.Queued q => q.Session,
            var other => throw new InvalidOperationException($"Unexpected {other}."),
        };

    private ProviderHealth Health(ControlledProvider? provider = null) =>
        _sessions.Health().Single(h => h.Provider == (provider ?? _provider).Name);

    /// <summary>The latest run of a session (a retry is a new run).</summary>
    private ControlledRun LastRun(Guid id) => _provider.Runs.Last(r => r.Launch.SessionId == id);

    /// <summary>Ends a session's latest run and waits for the runtime to take it in.</summary>
    private async Task<SessionRecord> End(Guid id, AgentOutcome outcome, Func<SessionRecord, bool> settled)
    {
        LastRun(id).End(outcome);
        return await Eventually(id, settled);
    }

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

    private List<ArmEvent> Events()
    {
        var events = new List<ArmEvent>();
        while (_events.Live.TryRead(out var stored))
        {
            events.Add(stored.Data);
        }

        return events;
    }

    [Test]
    public async Task EveryProvider_StartsAvailable()
    {
        await Assert.That(_sessions.Health().Select(h => h.Provider)).IsEquivalentTo(["controlled", "other"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(_sessions.Health().All(h => h.Status == ProviderStatus.Available && h.Reason is null && h.Since is null && h.Until is null)).IsTrue();
    }

    [Test]
    public async Task Refused_RequeuesTheSessionAtTheHead_AndPausesTheProvider()
    {
        var session = Create();
        var waiting = Create();
        _ = Create(); // queued behind
        Events();

        var requeued = await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);

        await Assert.That(requeued.QueuePosition).IsEqualTo(1);
        await Assert.That(requeued.Error).IsEqualTo("Unreachable for a while.");
        await Assert.That(requeued.Attempts).IsEqualTo(1);
        await Assert.That(requeued.FailedAttempts).IsEqualTo(1);
        var health = Health();
        await Assert.That(health.Status).IsEqualTo(ProviderStatus.Unreachable);
        await Assert.That(health.Reason).IsEqualTo("Unreachable for a while.");
        await Assert.That(health.Since).IsEqualTo(Start);
        await Assert.That(health.Until).IsEqualTo(Start + Backoff);
        var events = Events();
        await Assert.That(events.OfType<ProviderHealthChanged>().Single().Health).IsEquivalentTo(health);
        var queued = events.OfType<SessionQueued>().Single();
        await Assert.That(queued.SessionId).IsEqualTo(session.Id);
        await Assert.That(queued.Position).IsEqualTo(1);
        await Assert.That(queued.Error).IsEqualTo("Unreachable for a while.");
        // The freed slot stays free: the provider is paused.
        await Assert.That(_sessions.Get(waiting.Id)!.Status).IsEqualTo(SessionStatus.Working);
        await Assert.That(_provider.Runs).Count().IsEqualTo(2);
    }

    [Test]
    public async Task PausedProvider_QueuesNewSessions_WhileOtherProvidersStart()
    {
        var session = Create();
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);

        var created = _sessions.Create(new CreateSession { Prompt = "x", Provider = _provider.Name, Model = "m", Caller = "tests" });
        var other = Create(_other);

        await Assert.That(created).IsTypeOf<CreateOutcome.Queued>();
        await Assert.That(other.Status).IsEqualTo(SessionStatus.Working);
    }

    [Test]
    public async Task AfterTheWait_OneSessionTries_AndItsSuccessMakesTheProviderAvailable()
    {
        var session = Create();
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);
        var behind = Create();

        _time.Advance(Backoff);

        var retry = await Eventually(session.Id, s => s.Status == SessionStatus.Working);
        await Assert.That(retry.Attempts).IsEqualTo(2);
        await Assert.That(retry.Error).IsNull();
        // A retry gets a new provider session id; the first attempt used the session's own.
        await Assert.That(_provider.Runs[0].Launch.ProviderSessionId).IsEqualTo(session.Id);
        await Assert.That(LastRun(session.Id).Launch.Attempt).IsEqualTo(2);
        await Assert.That(LastRun(session.Id).Launch.ProviderSessionId).IsNotEqualTo(session.Id);
        // Only the probe: the session behind it waits for the outcome.
        await Assert.That(_sessions.Get(behind.Id)!.Status).IsEqualTo(SessionStatus.Queued);
        Events();

        await End(session.Id, new AgentOutcome.Completed(0), s => s.Status == SessionStatus.Completed);

        await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Available);
        await Eventually(behind.Id, s => s.Status == SessionStatus.Working);
        await Assert.That(Events().OfType<ProviderHealthChanged>().Single().Health.Status).IsEqualTo(ProviderStatus.Available);
    }

    [Test]
    public async Task RefusedAgain_WaitsLonger_UpToTheMaximum()
    {
        var session = Create();
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);
        _time.Advance(Backoff);
        await Eventually(session.Id, s => s.Status == SessionStatus.Working);

        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);

        await Assert.That(Health().Until).IsEqualTo(_time.GetUtcNow() + 2 * Backoff);
        await Assert.That(Health().Since).IsEqualTo(_time.GetUtcNow());
    }

    [Test]
    public async Task RefusedPastItsRetries_FailsWithTheLastError()
    {
        var session = Create();
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);
        _time.Advance(Backoff);
        await Eventually(session.Id, s => s.Status == SessionStatus.Working);
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);
        _time.Advance(2 * Backoff);
        await Eventually(session.Id, s => s.Status == SessionStatus.Working);

        var failed = await End(session.Id, Down(ProviderTrouble.Unauthorized), s => s.Status == SessionStatus.Failed);

        await Assert.That(failed.Error).IsEqualTo("Unauthorized for a while.");
        await Assert.That(failed.Attempts).IsEqualTo(3);
        await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Unauthorized);
    }

    [Test]
    public async Task Limited_StartsAgainWhenTheLimitResets_WithoutUsingUpRetries()
    {
        var resets = Start + TimeSpan.FromHours(3);
        var session = Create();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var requeued = await End(session.Id, Down(ProviderTrouble.Limited, resets), s => s.Status == SessionStatus.Queued);
            await Assert.That(requeued.FailedAttempts).IsEqualTo(0);
            await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Limited);
            await Assert.That(Health().Until).IsEqualTo(resets);

            _time.Advance(resets - _time.GetUtcNow() - TimeSpan.FromSeconds(1));
            await Assert.That(_sessions.Get(session.Id)!.Status).IsEqualTo(SessionStatus.Queued);
            _time.Advance(TimeSpan.FromSeconds(1));

            await Eventually(session.Id, s => s.Status == SessionStatus.Working);
            await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Available);
            resets = _time.GetUtcNow() + TimeSpan.FromHours(1);
        }
    }

    [Test]
    public async Task Limited_WithAResetInThePast_WaitsTheBackoff()
    {
        var session = Create();

        await End(session.Id, Down(ProviderTrouble.Limited, Start - TimeSpan.FromMinutes(5)), s => s.Status == SessionStatus.Queued);

        await Assert.That(Health().Until).IsEqualTo(Start + Backoff);
    }

    [Test]
    public async Task Unauthorized_WaitsForARestart()
    {
        var session = Create();

        await End(session.Id, Down(ProviderTrouble.Unauthorized), s => s.Status == SessionStatus.Queued);
        _time.Advance(TimeSpan.FromDays(1));

        await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Unauthorized);
        await Assert.That(Health().Until).IsNull();
        await Assert.That(_sessions.Get(session.Id)!.Status).IsEqualTo(SessionStatus.Queued);
    }

    [Test]
    public async Task RunsStartedBeforeAnOutage_DontStretchItsWait()
    {
        var first = Create();
        var second = Create();
        await End(first.Id, Down(), s => s.Status == SessionStatus.Queued);
        _time.Advance(TimeSpan.FromSeconds(10));

        await End(second.Id, Down(), s => s.Status == SessionStatus.Queued);

        var health = Health();
        await Assert.That(health.Since).IsEqualTo(Start);
        await Assert.That(health.Until).IsEqualTo(Start + Backoff);
        // Both wait; each at the head of the queue in the order they came back.
        await Assert.That(_sessions.Get(second.Id)!.QueuePosition).IsEqualTo(1);
        await Assert.That(_sessions.Get(first.Id)!.QueuePosition).IsEqualTo(2);
    }

    [Test]
    public async Task ARunStartedBeforeALimit_DoesntLiftIt()
    {
        var first = Create();
        var second = Create();
        await End(first.Id, Down(ProviderTrouble.Limited, Start + TimeSpan.FromHours(1)), s => s.Status == SessionStatus.Queued);

        await End(second.Id, new AgentOutcome.Completed(0), s => s.Status == SessionStatus.Completed);

        await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Limited);
    }

    [Test]
    public async Task AKilledProbe_LetsTheNextSessionTry()
    {
        var session = Create();
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);
        var next = Create();
        _time.Advance(Backoff);
        await Eventually(session.Id, s => s.Status == SessionStatus.Working);

        _sessions.Kill(session.Id, "no longer needed", KillSource.User, "tests");

        await Eventually(next.Id, s => s.Status == SessionStatus.Working);
        await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Unreachable);
    }

    [Test]
    public async Task AProbeFailingOnItsOwn_StillReachedTheProvider()
    {
        var session = Create();
        await End(session.Id, Down(), s => s.Status == SessionStatus.Queued);
        _time.Advance(Backoff);
        await Eventually(session.Id, s => s.Status == SessionStatus.Working);

        await End(session.Id, new AgentOutcome.Failed("bad prompt"), s => s.Status == SessionStatus.Failed);

        await Assert.That(Health().Status).IsEqualTo(ProviderStatus.Available);
    }
}
