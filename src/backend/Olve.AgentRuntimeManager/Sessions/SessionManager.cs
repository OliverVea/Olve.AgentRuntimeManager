using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions.Conversations;
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
/// <para>
/// Messages (M11, <see cref="Send"/>) go to a working session's agent at once; otherwise they're
/// held on the stored session (so they survive a restart) until its next agent starts, and an
/// ended session continues with them.
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
    /// Picks up the sessions the previous server left. Working ones get their agent back from their
    /// provider (gentle restart: re-attached, ended meanwhile, or resumed), keep their slot and
    /// what's left of their timeout; those whose agent is lost are killed, source <c>system</c>.
    /// Queued ones queue again in their order and start as slots allow. Call once, before serving
    /// requests.
    /// </summary>
    public void Recover()
    {
        var active = _store.Active();
        // Providers may talk to their agents' supervisors: not under the lock.
        var recovered = active.Where(s => s.Status == SessionStatus.Working).ToDictionary(s => s.Id, RecoverAgent);
        var timedOut = new List<SessionRecord>();
        lock (_gate)
        {
            foreach (var session in active)
            {
                if (session.Status == SessionStatus.Working)
                {
                    if (recovered[session.Id] is { } found)
                    {
                        if (!ReattachLocked(session, found.Agent, found.RunId))
                        {
                            timedOut.Add(session);
                        }

                        continue;
                    }

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
            _logger.LogInformation("Recovered {Working} working and {Queued} queued sessions", _running.Count, _queue.Count);
            StartNextLocked();
        }

        foreach (var session in timedOut)
        {
            Kill(session.Id, $"Timed out after {session.TimeoutSeconds}s.", KillSource.Timeout);
        }
    }

    /// <summary>How often a session its provider refused is retried (<see cref="SessionOptions.ProviderRetries"/>).</summary>
    public int ProviderRetries => _options.ProviderRetries;

    /// <summary>The names of the registered providers.</summary>
    public IReadOnlyList<string> Providers => [.. _providers.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Asks every provider whether it can run agents at all (<see cref="IAgentProvider.CheckAsync"/>)
    /// and pauses those that can't. A provider already paused by a session keeps its state.
    /// </summary>
    public async Task CheckProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers.Values)
        {
            if (await provider.CheckAsync(cancellationToken) is not { } unavailable)
            {
                continue;
            }

            lock (_gate)
            {
                var state = _health[provider.Name];
                if (state.Status == ProviderStatus.Available)
                {
                    PauseLocked(state, unavailable);
                }
            }
        }
    }

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
    /// <param name="agentEnv">The environment the agent gets, resolved from the registered variables and the request's own.</param>
    public CreateOutcome Create(CreateSession request, IReadOnlyDictionary<string, string>? agentEnv = null)
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
                Env = request.Env,
                UseEnv = request.UseEnv,
                AgentEnv = agentEnv is { Count: > 0 } ? agentEnv : null,
                CreatedAt = _time.GetUtcNow(),
            };
            _store.Add(session);
            _sessions[session.Id] = session;
            _events.Publish(new SessionCreated { At = session.CreatedAt, SessionId = session.Id, Session = session.ToDto(_options.ProviderRetries) });

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

    /// <summary>A session's conversation, from its provider; null if there's no such session.</summary>
    public IReadOnlyList<ConversationEntryRecord>? Conversation(Guid id)
    {
        if (Get(id) is not { } session)
        {
            return null;
        }

        // A provider no longer configured has nothing to read it from.
        return _providers.TryGetValue(session.Provider, out var provider) ? provider.Conversation(id) : [];
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

    /// <summary>
    /// Sends a message to a session's agent (M11). A working session's agent gets it now; a queued
    /// session holds it until its agent starts (after the prompt). An ended session continues with
    /// it: back to the end of the queue (<c>session.queued</c>, <c>previous</c> the state it had
    /// ended in), its outcome cleared, its agent to resume its provider session in a new run. A
    /// working session whose agent can't take it any more (its turn has just ended) holds it, and
    /// continues with it once that agent has ended. <paramref name="caller"/> is who sent it.
    /// </summary>
    public MessageOutcome Send(Guid id, string text, string caller)
    {
        lock (_gate)
        {
            _logger.LogInformation("Session {SessionId}: message from {Caller}", id, caller);
            if (_sessions.TryGetValue(id, out var session))
            {
                if (session.Status == SessionStatus.Queued)
                {
                    SaveLocked(Hold(session, text));
                    return new MessageOutcome.Pending();
                }

                // Messages already held go first: a new one mustn't overtake them.
                var running = _running[id];
                if (session.Messages is not { Count: > 0 } && running.Run.TrySend(text))
                {
                    running.Given.Add(text);
                    return new MessageOutcome.Delivered();
                }

                SaveLocked(Hold(session, text));
                return new MessageOutcome.Held();
            }

            if (_store.Get(id) is not { } ended)
            {
                return new MessageOutcome.NotFound();
            }

            if (ended.ProviderSessionId is null)
            {
                return new MessageOutcome.NeverStarted(ended);
            }

            if (!_providers.ContainsKey(ended.Provider))
            {
                return new MessageOutcome.UnknownProvider(ended);
            }

            ContinueLocked(Hold(ended, text));
            StartNextLocked();
            return new MessageOutcome.Continued();
        }
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

    /// <summary>A working session's agent from its provider, and the run it's in; null if it's lost.</summary>
    private (RecoveredAgent Agent, Guid RunId)? RecoverAgent(SessionRecord session)
    {
        if (session is not { RunId: { } runId, ProviderSessionId: { } providerSessionId } || !_providers.TryGetValue(session.Provider, out var provider))
        {
            return null;
        }

        var resumeRunId = Guid.NewGuid();
        try
        {
            return provider.Recover(new AgentRecovery(session.Id, session.Prompt, session.Model, providerSessionId, runId, resumeRunId, session.AgentEnv)) is { } agent
                ? (agent, agent.Resumed ? resumeRunId : runId)
                : null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Session {SessionId}: provider {Provider} could not recover the agent", session.Id, session.Provider);
            return null;
        }
    }

    /// <summary>
    /// A recovered agent takes its slot again, with what's left of its session's timeout. False if
    /// nothing is left: the caller kills it once the lock is released.
    /// </summary>
    private bool ReattachLocked(SessionRecord session, RecoveredAgent agent, Guid runId)
    {
        _sessions[session.Id] = session;
        if (session.RunId != runId)
        {
            SaveLocked(session with { RunId = runId });
        }

        TimeSpan? remaining = session is { TimeoutSeconds: { } seconds, StartedAt: { } startedAt }
            ? startedAt + TimeSpan.FromSeconds(seconds) - _time.GetUtcNow()
            : null;
        TrackLocked(session.Id, agent.Run, remaining > TimeSpan.Zero ? remaining : null);
        return remaining is not { } left || left > TimeSpan.Zero;
    }

    /// <summary>Starts a queued session's agent in a free slot (or fails the session if it can't start).</summary>
    private SessionRecord StartLocked(Guid id)
    {
        var session = _sessions[id];
        var attempt = session.Attempts + 1;
        // The session's own id first; a retry needs a new one (the provider may keep the old one's).
        var providerSessionId = attempt == 1 ? session.Id : Guid.NewGuid();
        // A session that has continued resumes its provider session, on every attempt from then on.
        var resume = session.ContinuedAt is not null ? session.ProviderSessionId : null;
        var messages = session.Messages;
        var runId = Guid.NewGuid();
        IAgentRun run;
        try
        {
            run = _providers[session.Provider].Start(new AgentLaunch(
                session.Id, session.Prompt, session.Model, attempt, providerSessionId, runId, session.AgentEnv, messages, resume));
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
            RunId = runId,
            Error = null,
            // The agent has them now (a failed start keeps them held).
            Messages = null,
        });
        TrackLocked(id, run, session.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, messages);
        _events.Publish(new SessionStarted { At = now, SessionId = id, Previous = session.Status, ProviderSessionId = run.ProviderSessionId });
        return started;
    }

    /// <summary>
    /// A working session's agent in its slot: counted for its provider, killed after
    /// <paramref name="timeout"/> (none: runs until it ends or is killed), finished when it ends.
    /// <paramref name="given"/>: the messages it got at its start.
    /// </summary>
    private void TrackLocked(Guid id, IAgentRun run, TimeSpan? timeout, IReadOnlyList<string>? given = null)
    {
        var session = _sessions[id];
        var provider = _health[session.Provider];
        provider.Started(id);
        var timer = timeout is { } due
            ? _time.CreateTimer(
                _ => Kill(id, $"Timed out after {session.TimeoutSeconds}s.", KillSource.Timeout),
                state: null,
                due,
                Timeout.InfiniteTimeSpan)
            : null;
        _running[id] = new RunningAgent(run, timer, provider.Generation, [.. given ?? []]);

        // Not awaited inline: the run may already be complete, and Finish takes the lock.
        _ = Task.Run(async () => Finish(id, run, await run.Completion));
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

            SessionRecord ended;
            switch (outcome)
            {
                case AgentOutcome.Unavailable unavailable:
                    if (current || probe)
                    {
                        PauseLocked(provider, unavailable);
                    }

                    // The agent did nothing: what it was told goes back to the session, for its next try.
                    RetryOrFailLocked(running.Given.Count > 0 ? session with { Messages = [.. running.Given, .. session.Messages ?? []] } : session, unavailable);
                    StartNextLocked();
                    return;
                case AgentOutcome.Completed completed:
                    ended = EndLocked(session, SessionStatus.Completed, s => s with { ExitCode = completed.ExitCode });
                    _events.Publish(new SessionCompleted
                    {
                        At = ended.EndedAt!.Value, SessionId = id, Previous = session.Status,
                        ExitCode = completed.ExitCode,
                    });
                    break;
                case AgentOutcome.Failed failed:
                    ended = EndLocked(session, SessionStatus.Failed, s => s with { Error = failed.Error });
                    _events.Publish(new SessionFailed { At = ended.EndedAt!.Value, SessionId = id, Previous = session.Status, Error = failed.Error });
                    break;
                default:
                    // Stopped from outside ARM.
                    const string reason = "The agent stopped.";
                    ended = EndLocked(session, SessionStatus.Killed, s => s with { KillReason = reason, KillSource = KillSource.System });
                    _events.Publish(new SessionKilled
                    {
                        At = ended.EndedAt!.Value, SessionId = id, Previous = session.Status, Reason = reason, Source = KillSource.System,
                    });
                    break;
            }

            // Messages its agent could no longer take: the session continues with them.
            if (ended.Messages is { Count: > 0 })
            {
                ContinueLocked(ended);
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
        _events.Publish(new SessionQueued { At = _time.GetUtcNow(), SessionId = session.Id, Position = 1, Error = unavailable.Error, Previous = session.Status });
    }

    /// <summary>
    /// An ended session continues (M11): back to the end of the queue, like a new one, with its
    /// outcome cleared and a fresh provider-retry budget; it keeps its id, conversation and held
    /// messages. The queue's cap doesn't apply: the session was let in once already.
    /// </summary>
    private void ContinueLocked(SessionRecord ended)
    {
        var now = _time.GetUtcNow();
        SaveLocked(ended with
        {
            Status = SessionLifecycle.Move(ended.Status, SessionStatus.Queued),
            ContinuedAt = now,
            EndedAt = null,
            ExitCode = null,
            Error = null,
            KillReason = null,
            KillSource = null,
            KillCaller = null,
            FailedAttempts = 0,
        });
        _queue.Add(ended.Id);
        RenumberQueueLocked();
        _events.Publish(new SessionQueued { At = now, SessionId = ended.Id, Position = _queue.Count, Previous = ended.Status });
    }

    /// <summary>The session holding one more message for its agent.</summary>
    private static SessionRecord Hold(SessionRecord session, string text) =>
        session with { Messages = [.. session.Messages ?? [], text] };

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
