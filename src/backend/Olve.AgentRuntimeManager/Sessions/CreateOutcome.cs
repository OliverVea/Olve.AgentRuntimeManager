namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>What <see cref="SessionManager.Create"/> did.</summary>
public abstract record CreateOutcome
{
    private CreateOutcome()
    {
    }

    /// <summary>A slot was free: the session is working (or failed to start).</summary>
    public sealed record Started(SessionRecord Session) : CreateOutcome;

    /// <summary>Every slot is busy: the session waits in the queue.</summary>
    public sealed record Queued(SessionRecord Session) : CreateOutcome;

    /// <summary>The queue is full; nothing was created.</summary>
    public sealed record QueueFull(int MaxQueueSize) : CreateOutcome;

    /// <summary>No provider has this name; nothing was created.</summary>
    public sealed record UnknownProvider(string Provider, IReadOnlyList<string> Known) : CreateOutcome;
}
