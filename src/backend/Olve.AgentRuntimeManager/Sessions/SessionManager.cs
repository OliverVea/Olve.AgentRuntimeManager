using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// The session runtime: every session (in the <see cref="ISessionStore"/>), a FIFO queue in front
/// of <see cref="SessionOptions.TotalSlots"/> slots, the agents running in them, each provider's
/// health (<see cref="ProviderState"/>), and an event on the <see cref="EventBus"/> for every
/// change (session moves follow <see cref="SessionLifecycle"/>).
/// </summary>
/// <remarks>
/// One lock guards all state, and events are published under it, so their order is the order of
/// the changes. Agents are started under the lock too (<see cref="IAgentProvider.Start"/> must not
/// block); their completion, kills after the lock is released, and timeouts come back through
/// <see cref="Finish"/> and <see cref="Kill"/>. Every change is stored before memory changes and
/// before its event is published, so an event never announces what isn't stored. Queued and
/// working sessions are also kept in memory (with their queue positions, which aren't stored);
/// ended ones are read from the store.
/// <para>
/// A provider that refuses an agent (<see cref="AgentOutcome.Unavailable"/>: out of usage, down,
/// bad credentials) pauses: its queued sessions wait while other providers' sessions pass them,
/// and the refused session goes back to the head of the queue, until it has used up
/// <see cref="SessionOptions.ProviderRetries"/>. Provider health lives in memory only.
/// </para>
/// </remarks>
public sealed class SessionManager : IDisposable
{
    private readonly Lock _gate = new();
    private readonly EventBus _events;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;
    private readonly ILogger<SessionManager> _logger;
    private readonly ISessionStore _store;

    /// <summary>The queued and working sessions.</summary>
    private readonly Dictionary<Guid, SessionRecord> _sessions = [];
    private readonly List<Guid> _queue = [];
    private readonly Dictionary<Guid, RunningAgent> _running = [];
    private readonly IReadOnlyDictionary<string, ProviderState> _health;

    public SessionManager(
        EventBus events,
        TimeProvider time,
        IOptions<SessionOptions> options,
        IEnumerable<IAgentProvider> providers,
        ISessionStore store,
        ILogger<SessionManager> logger)
    {
        _store = store;
        _events = events;
        _time = time;
        _options = options.Value;
        _providers = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _health = _providers.Keys.ToDictionary(name => name, name => new ProviderState(name), StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>
    /// Picks up the sessions the previous server left: working ones lost their agent (until gentle
    /// restart re-attaches them) and are killed, source <c>system</c>; queued ones queue again in
    /// their order and start as slots allow. Call once, before serving requests.
    /// </summary>
    public void Recover()
    {
        lock (_gate)
        {
            foreach (var session in _store.Active())
            {
                if (session.Status == SessionStatus.Working)
                {
                    const string reason = "ARM restarted; the agent was lost.";
                    var killed = EndLocked(session, SessionStatus.Killed, s => s with { KillReason = reason, KillSource = KillSource.System });
                    _events.Publish(new SessionKilled
                    {
                        At = killed.EndedAt!.Value, SessionId = session.Id, Previous = session.Status, Reason = reason, Source = KillSource.System,
                    });
                    continue;
                }

                _sessions[session.Id] = session;
                _queue.Add(session.Id);
            }

            RenumberQueueLocked();
            _logger.LogInformation("Recovered {Queued} queued sessions", _queue.Count);
            StartNextLocked();
        }
    }

    /// <summary>The names of the registered providers.</summary>
    public IReadOnlyList<string> Providers => [.. _providers.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Every provider's health, by name.</summary>
    public IReadOnlyList<ProviderHealth> Health()
    {
        lock (_gate)
        {
            return [.. _health.Values.OrderBy(h => h.Name, StringComparer.Ordinal).Select(h => h.ToDto())];
        }
    }

    /// <summary>
    /// Creates a session and starts it if a slot is free and its provider available, else queues it. Publishes
    /// <c>session.created</c>, then <c>session.started</c> (or <c>session.failed</c> if the agent
    /// can't start) or <c>session.queued</c>.
    /// </summary>
    public CreateOutcome Create(CreateSession request)
    {
        if (!_providers.ContainsKey(request.Provider))
        {
            return new CreateOutcome.UnknownProvider(request.Provider, Providers);
        }

        lock (_gate)
        {
            var startNow = _running.Count < _options.TotalSlots && _health[request.Provider].MayStart;
            if (!startNow && _queue.Count >= _options.MaxQueueSize)
            {
                return new CreateOutcome.QueueFull(_options.MaxQueueSize);
            }

            var session = new SessionRecord
            {
                Id = Guid.NewGuid(),
                Status = SessionStatus.Queued,
                Prompt = request.Prompt,
                Provider = request.Provider,
                Model = request.Model,
                Caller = request.Caller,
                TimeoutSeconds = request.TimeoutSeconds,
                CreatedAt = _time.GetUtcNow(),
            };
            _store.Add(session);
            _sessions[session.Id] = session;
            _events.Publish(new SessionCreated { At = session.CreatedAt, SessionId = session.Id, Session = session.ToDto() });

            if (startNow)
            {
                return new CreateOutcome.Started(StartLocked(session.Id));
            }

            _queue.Add(session.Id);
            RenumberQueueLocked();
            _events.Publish(new SessionQueued { At = _time.GetUtcNow(), SessionId = session.Id, Position = _queue.Count });
            return new CreateOutcome.Queued(_sessions[session.Id]);
        }
    }

    public SessionRecord? Get(Guid id)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var active))
            {
                return active;
            }
        }

        return _store.Get(id);
    }

    /// <summary>Sessions matching every given filter, newest first, one page at a time.</summary>
    public SessionQueryResult Search(SessionSearch search, int limit, int offset)
    {
        var page = _store.Search(search, limit, offset);
        lock (_gate)
        {
            // Active sessions as memory has them: with their queue positions.
            return page with { Items = [.. page.Items.Select(s => _sessions.GetValueOrDefault(s.Id) ?? s)] };
        }
    }

    /// <summary>
    /// Stops a session: a queued one is cancelled (<c>session.cancelled</c>), a working one is
    /// killed (<c>session.killed</c>) and the next queued one starts in its slot.
    /// <paramref name="caller"/> is who asked, for stops by a user.
    /// </summary>
    public KillOutcome Kill(Guid id, string? reason, KillSource source, string? caller = null)
    {
        IAgentRun? run = null;
        KillOutcome outcome;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
            {
                return _store.Get(id) is { } ended
                    ? new KillOutcome.AlreadyEnded(ended)
                    : new KillOutcome.NotFound();
            }

            if (_queue.Remove(id))
            {
                RenumberQueueLocked();
            }

            if (_running.Remove(id, out var running))
            {
                running.Timeout?.Dispose();
                run = running.Run;
                _health[session.Provider].Ended(id);
            }

            var now = _time.GetUtcNow();
            var cancel = session.Status == SessionStatus.Queued;
            var stopped = session with
            {
                Status = SessionLifecycle.Move(session.Status, cancel ? SessionStatus.Cancelled : SessionStatus.Killed),
                QueuePosition = null,
                EndedAt = now,
                KillReason = reason,
                KillSource = source,
                KillCaller = caller,
            };
            SaveLocked(stopped);
            _events.Publish(cancel
                ? new SessionCancelled { At = now, SessionId = id, Previous = session.Status, Reason = reason, Source = source, Caller = caller }
                : new SessionKilled { At = now, SessionId = id, Previous = session.Status, Reason = reason, Source = source, Caller = caller });
            StartNextLocked();
            outcome = new KillOutcome.Stopped(stopped);
        }

        run?.Kill();
        return outcome;
    }

    /// <summary>Deletes a session that has ended.</summary>
    public DeleteOutcome Delete(Guid id)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var active))
            {
                return new DeleteOutcome.NotEnded(active);
            }

            if (_store.Get(id) is null)
            {
                return new DeleteOutcome.NotFound();
            }

            _store.Delete(id);
            return new DeleteOutcome.Deleted();
        }
    }

    /// <summary>Stops the timeouts and provider wake-ups; the agents themselves are left running (gentle restart, M5a).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var running in _running.Values)
            {
                running.Timeout?.Dispose();
            }

            foreach (var provider in _health.Values)
            {
                provider.Timer?.Dispose();
            }
        }
    }

    /// <summary>Starts a queued session's agent in a free slot (or fails the session if it can't start).</summary>
    private SessionRecord StartLocked(Guid id)
    {
        var session = _sessions[id];
        var attempt = session.Attempts + 1;
        // The session's own id first; a retry needs a new one (the provider may keep the old one's).
        var providerSessionId = attempt == 1 ? session.Id : Guid.NewGuid();
        IAgentRun run;
        try
        {
            run = _providers[session.Provider].Start(new AgentLaunch(session.Id, session.Prompt, session.Model, attempt, providerSessionId));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Session {SessionId}: provider {Provider} could not start the agent", id, session.Provider);
            var failed = EndLocked(session, SessionStatus.Failed, s => s with { Error = exception.Message });
            _events.Publish(new SessionFailed { At = failed.EndedAt!.Value, SessionId = id, Previous = session.Status, Error = exception.Message });
            return failed;
        }

        var now = _time.GetUtcNow();
        var started = SaveLocked(session with
        {
            Status = SessionLifecycle.Move(session.Status, SessionStatus.Working),
            QueuePosition = null,
            Attempts = attempt,
            StartedAt = now,
            ProviderSessionId = run.ProviderSessionId,
            Error = null,
        });
        var provider = _health[session.Provider];
        provider.Started(id);
        // No timeout: no timer (the session runs until it ends or is killed).
        var timer = session.TimeoutSeconds is { } seconds
            ? _time.CreateTimer(
                _ => Kill(id, $"Timed out after {seconds}s.", KillSource.Timeout),
                state: null,
                TimeSpan.FromSeconds(seconds),
                Timeout.InfiniteTimeSpan)
            : null;
        _running[id] = new RunningAgent(run, timer, provider.Generation);
        _events.Publish(new SessionStarted { At = now, SessionId = id, Previous = session.Status, ProviderSessionId = run.ProviderSessionId });

        // Not awaited inline: the run may already be complete, and Finish takes the lock.
        _ = Task.Run(async () => Finish(id, run, await run.Completion));
        return started;
    }

    /// <summary>An agent ended on its own: completes or fails its session and frees its slot.</summary>
    private void Finish(Guid id, IAgentRun run, AgentOutcome outcome)
    {
        lock (_gate)
        {
            // Killed (or already finished) sessions have left _running: nothing to record.
            if (!_running.TryGetValue(id, out var running) || running.Run != run)
            {
                return;
            }

            _running.Remove(id);
            running.Timeout?.Dispose();
            var session = _sessions[id];
            var provider = _health[session.Provider];
            var current = running.Generation == provider.Generation;
            var probe = provider.Ended(id);
            // The probe got through to the provider (a failure of its own still reached it): it's back.
            if (probe && outcome is AgentOutcome.Completed or AgentOutcome.Failed && provider.Status == ProviderStatus.Unreachable)
            {
                RecoverLocked(provider);
            }

            switch (outcome)
            {
                case AgentOutcome.Unavailable unavailable:
                    if (current || probe)
                    {
                        PauseLocked(provider, unavailable);
                    }

                    RetryOrFailLocked(session, unavailable);
                    break;
                case AgentOutcome.Completed completed:
                    var done = EndLocked(session, SessionStatus.Completed, s => s with { ExitCode = completed.ExitCode });
                    _events.Publish(new SessionCompleted
                    {
                        At = done.EndedAt!.Value, SessionId = id, Previous = session.Status,
                        ExitCode = completed.ExitCode,
                    });
                    break;
                case AgentOutcome.Failed failed:
                    var failedSession = EndLocked(session, SessionStatus.Failed, s => s with { Error = failed.Error });
                    _events.Publish(new SessionFailed { At = failedSession.EndedAt!.Value, SessionId = id, Previous = session.Status, Error = failed.Error });
                    break;
                default:
                    // Stopped from outside ARM.
                    const string reason = "The agent stopped.";
                    var stopped = EndLocked(session, SessionStatus.Killed, s => s with { KillReason = reason, KillSource = KillSource.System });
                    _events.Publish(new SessionKilled
                    {
                        At = stopped.EndedAt!.Value, SessionId = id, Previous = session.Status, Reason = reason, Source = KillSource.System,
                    });
                    break;
            }

            StartNextLocked();
        }
    }

    /// <summary>
    /// Pauses a provider that refused an agent: until a usage limit resets (<c>limited</c>), for a
    /// growing wait before one session tries again (<c>unreachable</c>), or until a restart
    /// (<c>unauthorized</c>). Publishes <c>provider.health</c>.
    /// </summary>
    private void PauseLocked(ProviderState provider, AgentOutcome.Unavailable unavailable)
    {
        var now = _time.GetUtcNow();
        provider.Timer?.Dispose();
        provider.Timer = null;
        switch (unavailable)
        {
            case { Trouble: ProviderTrouble.Unauthorized }:
                provider.Enter(ProviderStatus.Unauthorized, unavailable.Error, now, until: null);
                break;
            case { Trouble: ProviderTrouble.Limited, Until: { } resetsAt }:
                // A reset already past (clock skew): a short wait rather than a busy loop.
                provider.Enter(ProviderStatus.Limited, unavailable.Error, now, resetsAt > now ? resetsAt : now + _options.ProviderBackoff);
                break;
            default:
                // Unreachable, or limited without saying until when: wait, longer each time in a row.
                var failures = provider.Status == ProviderStatus.Unreachable ? provider.Failures + 1 : 1;
                var wait = TimeSpan.FromTicks(Math.Min(
                    _options.ProviderBackoff.Ticks * (1L << Math.Min(failures - 1, 20)),
                    _options.ProviderMaxBackoff.Ticks));
                provider.Enter(ProviderStatus.Unreachable, unavailable.Error, now, now + wait);
                break;
        }

        if (provider.Until is { } until)
        {
            var (name, generation) = (provider.Name, provider.Generation);
            provider.Timer = _time.CreateTimer(_ => Wake(name, generation), state: null, until - now, Timeout.InfiniteTimeSpan);
        }

        _logger.LogWarning("Provider {Provider} is {Status} until {Until}: {Reason}", provider.Name, provider.Status, provider.Until, unavailable.Error);
        _events.Publish(new ProviderHealthChanged { At = now, Health = provider.ToDto() });
    }

    private void RecoverLocked(ProviderState provider)
    {
        provider.Timer?.Dispose();
        provider.Timer = null;
        provider.Recover();
        _logger.LogInformation("Provider {Provider} is available again", provider.Name);
        _events.Publish(new ProviderHealthChanged { At = _time.GetUtcNow(), Health = provider.ToDto() });
    }

    /// <summary>
    /// A paused provider's wait is over: a usage limit has reset, or an unreachable provider may be
    /// tried again. A timer of a state that has changed since (it may fire just as it's replaced) does nothing.
    /// </summary>
    private void Wake(string name, int generation)
    {
        lock (_gate)
        {
            var provider = _health[name];
            if (provider.Generation != generation)
            {
                return;
            }

            if (provider.Status == ProviderStatus.Limited)
            {
                RecoverLocked(provider);
            }
            else
            {
                provider.EndWait();
            }

            StartNextLocked();
        }
    }

    /// <summary>
    /// A session its provider refused goes back to the head of the queue (<c>session.queued</c>
    /// with the error), unless it has used up its retries: then it fails with that error. Waiting
    /// for a known usage-limit reset uses up nothing.
    /// </summary>
    private void RetryOrFailLocked(SessionRecord session, AgentOutcome.Unavailable unavailable)
    {
        var failedAttempts = session.FailedAttempts + (unavailable is { Trouble: ProviderTrouble.Limited, Until: not null } ? 0 : 1);
        if (failedAttempts > _options.ProviderRetries)
        {
            var failed = EndLocked(session, SessionStatus.Failed, s => s with { Error = unavailable.Error, FailedAttempts = failedAttempts });
            _events.Publish(new SessionFailed { At = failed.EndedAt!.Value, SessionId = session.Id, Previous = session.Status, Error = unavailable.Error });
            return;
        }

        SaveLocked(session with
        {
            Status = SessionLifecycle.Move(session.Status, SessionStatus.Queued),
            Error = unavailable.Error,
            FailedAttempts = failedAttempts,
        });
        _queue.Insert(0, session.Id);
        RenumberQueueLocked();
        _events.Publish(new SessionQueued { At = _time.GetUtcNow(), SessionId = session.Id, Position = 1, Error = unavailable.Error });
    }

    private SessionRecord EndLocked(SessionRecord session, SessionStatus status, Func<SessionRecord, SessionRecord> details) =>
        SaveLocked(details(session with
        {
            Status = SessionLifecycle.Move(session.Status, status),
            QueuePosition = null,
            EndedAt = _time.GetUtcNow(),
        }));

    /// <summary>Stores a session's new state, then keeps it in memory while it's queued or working.</summary>
    private SessionRecord SaveLocked(SessionRecord session)
    {
        _store.Update(session);
        if (SessionLifecycle.IsTerminal(session.Status))
        {
            _sessions.Remove(session.Id);
        }
        else
        {
            _sessions[session.Id] = session;
        }

        return session;
    }

    /// <summary>
    /// Fills free slots from the head of the queue, passing over sessions whose provider is paused
    /// (each provider's sessions still start in their order).
    /// </summary>
    private void StartNextLocked()
    {
        while (_running.Count < _options.TotalSlots)
        {
            var index = _queue.FindIndex(id => _health[_sessions[id].Provider].MayStart);
            if (index < 0)
            {
                return;
            }

            var next = _queue[index];
            _queue.RemoveAt(index);
            RenumberQueueLocked();
            StartLocked(next);
        }
    }

    private void RenumberQueueLocked()
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            _sessions[_queue[i]] = _sessions[_queue[i]] with { QueuePosition = i + 1 };
        }
    }
}
