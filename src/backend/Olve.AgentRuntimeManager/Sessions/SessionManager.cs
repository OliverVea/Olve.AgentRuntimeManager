using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// The session runtime: every session, a FIFO queue in front of <see cref="SessionOptions.TotalSlots"/>
/// slots, the agents running in them, and a lifecycle event on the <see cref="EventBus"/> for every
/// state change (moves follow <see cref="SessionLifecycle"/>).
/// </summary>
/// <remarks>
/// One lock guards all state, and events are published under it, so their order is the order of
/// the changes. Agents are started under the lock too (<see cref="IAgentProvider.Start"/> must not
/// block); their completion, kills after the lock is released, and timeouts come back through
/// <see cref="Finish"/> and <see cref="Kill"/>. Sessions live in memory until M5 persists them.
/// </remarks>
public sealed class SessionManager : IDisposable
{
    private readonly Lock _gate = new();
    private readonly EventBus _events;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;
    private readonly ILogger<SessionManager> _logger;
    private readonly Dictionary<Guid, SessionRecord> _sessions = [];
    private readonly List<Guid> _queue = [];
    private readonly Dictionary<Guid, RunningAgent> _running = [];

    public SessionManager(
        EventBus events,
        TimeProvider time,
        IOptions<SessionOptions> options,
        IEnumerable<IAgentProvider> providers,
        ILogger<SessionManager> logger)
    {
        _events = events;
        _time = time;
        _options = options.Value;
        _providers = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>The names of the registered providers.</summary>
    public IReadOnlyList<string> Providers => [.. _providers.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Creates a session and starts it if a slot is free, else queues it. Publishes
    /// <c>session.created</c>, then <c>session.started</c> (or <c>session.failed</c> if the agent
    /// can't start) or <c>session.queued</c>.
    /// </summary>
    public CreateOutcome Create(CreateSession request)
    {
        var providerName = request.Provider ?? _options.DefaultProvider;
        if (!_providers.ContainsKey(providerName))
        {
            return new CreateOutcome.UnknownProvider(providerName, Providers);
        }

        lock (_gate)
        {
            var slotFree = _running.Count < _options.TotalSlots;
            if (!slotFree && _queue.Count >= _options.MaxQueueSize)
            {
                return new CreateOutcome.QueueFull(_options.MaxQueueSize);
            }

            var session = new SessionRecord
            {
                Id = Guid.NewGuid(),
                Status = SessionStatus.Queued,
                Prompt = request.Prompt,
                Provider = providerName,
                Model = request.Model,
                Effort = request.Effort,
                SystemPrompt = request.SystemPrompt,
                Caller = request.Caller,
                Tags = request.Tags ?? new Dictionary<string, string>(),
                Env = request.Env ?? new Dictionary<string, string>(),
                SecretEnv = request.SecretEnv ?? new Dictionary<string, string>(),
                TimeoutSeconds = request.TimeoutSeconds ?? _options.DefaultTimeoutSeconds,
                ApprovalPolicy = request.ApprovalPolicy,
                Tools = request.Tools ?? [],
                Skills = request.Skills ?? [],
                Messaging = request.Messaging ?? true,
                Headless = request.Headless ?? false,
                CreatedAt = _time.GetUtcNow(),
            };
            _sessions[session.Id] = session;
            _events.Publish(new SessionCreated { At = session.CreatedAt, SessionId = session.Id, Session = session.ToDto() });

            if (slotFree)
            {
                StartLocked(session.Id);
                return new CreateOutcome.Started(_sessions[session.Id]);
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
            return _sessions.GetValueOrDefault(id);
        }
    }

    /// <summary>Sessions matching every given filter, newest first, one page at a time.</summary>
    public SessionQueryResult Search(SessionSearch search, int limit, int offset)
    {
        List<SessionRecord> all;
        lock (_gate)
        {
            all = [.. _sessions.Values];
        }

        var matches = all
            .Where(s => search.Status is not { } status || s.Status == status)
            .Where(s => search.Caller is not { } caller || s.Caller == caller)
            .Where(s => search.Tags is not { } tags || tags.All(t => s.Tags.TryGetValue(t.Key, out var v) && v == t.Value))
            .Where(s => search.CreatedAfter is not { } after || s.CreatedAt > after)
            .Where(s => search.CreatedBefore is not { } before || s.CreatedAt < before)
            .Where(s => search.Text is not { } text || s.Prompt.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToList();

        return new SessionQueryResult([.. matches.Skip(offset).Take(limit)], matches.Count, limit, offset);
    }

    /// <summary>
    /// Kills a queued, working or waiting session (<c>session.killed</c>) and starts the next
    /// queued one in its slot.
    /// </summary>
    public KillOutcome Kill(Guid id, string? reason, KillSource source)
    {
        IAgentRun? run = null;
        KillOutcome outcome;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
            {
                return new KillOutcome.NotFound();
            }

            if (SessionLifecycle.IsTerminal(session.Status))
            {
                return new KillOutcome.AlreadyEnded(session);
            }

            if (_queue.Remove(id))
            {
                RenumberQueueLocked();
            }

            if (_running.Remove(id, out var running))
            {
                running.Timeout.Dispose();
                run = running.Run;
            }

            var now = _time.GetUtcNow();
            var killed = session with
            {
                Status = SessionLifecycle.Move(session.Status, SessionStatus.Killed),
                QueuePosition = null,
                EndedAt = now,
                KillReason = reason,
                KillSource = source,
            };
            _sessions[id] = killed;
            _events.Publish(new SessionKilled { At = now, SessionId = id, Previous = session.Status, Reason = reason, Source = source });
            StartNextLocked();
            outcome = new KillOutcome.Killed(_sessions[id]);
        }

        run?.Kill();
        return outcome;
    }

    /// <summary>Deletes a session that has ended.</summary>
    public DeleteOutcome Delete(Guid id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
            {
                return new DeleteOutcome.NotFound();
            }

            if (!SessionLifecycle.IsTerminal(session.Status))
            {
                return new DeleteOutcome.NotEnded(session);
            }

            _sessions.Remove(id);
            return new DeleteOutcome.Deleted();
        }
    }

    /// <summary>Stops the timeouts; the agents themselves are left running (gentle restart, M5).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var running in _running.Values)
            {
                running.Timeout.Dispose();
            }
        }
    }

    /// <summary>Starts a queued session's agent in a free slot (or fails the session if it can't start).</summary>
    private void StartLocked(Guid id)
    {
        var session = _sessions[id];
        IAgentRun run;
        try
        {
            run = _providers[session.Provider].Start(new AgentLaunch(
                session.Id, session.Prompt, session.Model, session.Effort, session.SystemPrompt, session.Env));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Session {SessionId}: provider {Provider} could not start the agent", id, session.Provider);
            EndLocked(session, SessionStatus.Failed, s => s with { Error = exception.Message });
            _events.Publish(new SessionFailed { At = _sessions[id].EndedAt!.Value, SessionId = id, Previous = session.Status, Error = exception.Message });
            return;
        }

        var now = _time.GetUtcNow();
        _sessions[id] = session with
        {
            Status = SessionLifecycle.Move(session.Status, SessionStatus.Working),
            QueuePosition = null,
            StartedAt = now,
            ProviderSessionId = run.ProviderSessionId,
        };
        var timeout = TimeSpan.FromSeconds(session.TimeoutSeconds);
        var timer = _time.CreateTimer(
            _ => Kill(id, $"Timed out after {session.TimeoutSeconds}s.", KillSource.Timeout),
            state: null,
            timeout,
            Timeout.InfiniteTimeSpan);
        _running[id] = new RunningAgent(run, timer);
        _events.Publish(new SessionStarted { At = now, SessionId = id, Previous = session.Status, ProviderSessionId = run.ProviderSessionId });

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
            running.Timeout.Dispose();
            var session = _sessions[id];
            switch (outcome)
            {
                case AgentOutcome.Completed completed:
                    EndLocked(session, SessionStatus.Completed, s => s with { ExitCode = completed.ExitCode, Summary = completed.Summary });
                    _events.Publish(new SessionCompleted
                    {
                        At = _sessions[id].EndedAt!.Value, SessionId = id, Previous = session.Status,
                        ExitCode = completed.ExitCode, Summary = completed.Summary,
                    });
                    break;
                case AgentOutcome.Failed failed:
                    EndLocked(session, SessionStatus.Failed, s => s with { Error = failed.Error });
                    _events.Publish(new SessionFailed { At = _sessions[id].EndedAt!.Value, SessionId = id, Previous = session.Status, Error = failed.Error });
                    break;
                default:
                    // Stopped from outside ARM.
                    const string reason = "The agent stopped.";
                    EndLocked(session, SessionStatus.Killed, s => s with { KillReason = reason, KillSource = KillSource.System });
                    _events.Publish(new SessionKilled
                    {
                        At = _sessions[id].EndedAt!.Value, SessionId = id, Previous = session.Status, Reason = reason, Source = KillSource.System,
                    });
                    break;
            }

            StartNextLocked();
        }
    }

    private void EndLocked(SessionRecord session, SessionStatus status, Func<SessionRecord, SessionRecord> details) =>
        _sessions[session.Id] = details(session with
        {
            Status = SessionLifecycle.Move(session.Status, status),
            QueuePosition = null,
            EndedAt = _time.GetUtcNow(),
        });

    /// <summary>Fills free slots from the head of the queue.</summary>
    private void StartNextLocked()
    {
        while (_running.Count < _options.TotalSlots && _queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
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
