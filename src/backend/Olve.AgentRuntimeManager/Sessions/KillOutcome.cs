namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>What <see cref="SessionManager.Kill"/> did.</summary>
public abstract record KillOutcome
{
    private KillOutcome()
    {
    }

    /// <summary>The session was stopped: cancelled if it was queued, killed if it was working.</summary>
    public sealed record Stopped(SessionRecord Session) : KillOutcome;

    public sealed record NotFound : KillOutcome;

    /// <summary>The session had already ended; it is unchanged.</summary>
    public sealed record AlreadyEnded(SessionRecord Session) : KillOutcome;
}
