using Microsoft.Extensions.Logging;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>
/// The supervisors a test's <see cref="Supervisors"/> started, by pid, from the moment each starts:
/// the logger the test gives it notes ARM's "supervisor {Pid} started". This is how a test knows
/// what is its own to kill, without looking through <c>/proc</c>: each supervisor leads a process
/// group of its own (it calls setsid), which holds its agent and whatever that starts.
/// </summary>
public sealed class StartedSupervisors : ILogger<Supervisors>
{
    private readonly List<(int Pid, Guid SessionId)> _started;
    private readonly ILogger<Supervisors>? _inner;

    public StartedSupervisors()
        : this(null, [])
    {
    }

    private StartedSupervisors(ILogger<Supervisors>? inner, List<(int Pid, Guid SessionId)> started)
    {
        _inner = inner;
        _started = started;
    }

    /// <summary>A logger noting into the same list, that passes everything on to <paramref name="inner"/> too.</summary>
    public StartedSupervisors Passing(ILogger<Supervisors> inner) => new(inner, _started);

    /// <summary>Every supervisor started so far.</summary>
    public IReadOnlyList<int> Pids
    {
        get
        {
            lock (_started)
            {
                return [.. _started.Select(s => s.Pid)];
            }
        }
    }

    /// <summary>The supervisors started for session <paramref name="sessionId"/>.</summary>
    public IReadOnlyList<int> Of(Guid sessionId)
    {
        lock (_started)
        {
            return [.. _started.Where(s => s.SessionId == sessionId).Select(s => s.Pid)];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner?.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values
            && values.Any(v => v is { Key: "{OriginalFormat}", Value: string format } && format.Contains("supervisor {Pid} started", StringComparison.Ordinal))
            && values.FirstOrDefault(v => v.Key == "Pid").Value is int pid
            && values.FirstOrDefault(v => v.Key == "SessionId").Value is Guid sessionId)
        {
            lock (_started)
            {
                _started.Add((pid, sessionId));
            }
        }

        if (_inner?.IsEnabled(logLevel) == true)
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
