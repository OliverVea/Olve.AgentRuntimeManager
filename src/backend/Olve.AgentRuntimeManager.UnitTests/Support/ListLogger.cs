using Microsoft.Extensions.Logging;

namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>A logger that keeps what it's told, for a test to show (every level).</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages)
        {
            _messages.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
        }
    }
}
