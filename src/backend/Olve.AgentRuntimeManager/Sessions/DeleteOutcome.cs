namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>What <see cref="SessionManager.Delete"/> did.</summary>
public abstract record DeleteOutcome
{
    private DeleteOutcome()
    {
    }

    public sealed record Deleted : DeleteOutcome;

    public sealed record NotFound : DeleteOutcome;

    /// <summary>The session hasn't ended (only terminal sessions can be deleted); it is unchanged.</summary>
    public sealed record NotEnded(SessionRecord Session) : DeleteOutcome;
}
