namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>What <see cref="SessionManager.Kill"/> did.</summary>
public abstract record KillOutcome
{
    private KillOutcome()
    {
    }

    public sealed record Killed(SessionRecord Session) : KillOutcome;

    public sealed record NotFound : KillOutcome;

    /// <summary>The session had already ended; it is unchanged.</summary>
    public sealed record AlreadyEnded(SessionRecord Session) : KillOutcome;
}
