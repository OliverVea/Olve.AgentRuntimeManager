namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>What <see cref="SessionManager.Send"/> did with a message.</summary>
public abstract record MessageOutcome
{
    private MessageOutcome()
    {
    }

    /// <summary>The session is working: its agent got the message.</summary>
    public sealed record Delivered : MessageOutcome;

    /// <summary>The session is queued: the message is held until its agent starts.</summary>
    public sealed record Pending : MessageOutcome;

    /// <summary>The session had ended: it continues with the message (queued again).</summary>
    public sealed record Continued : MessageOutcome;

    /// <summary>
    /// The session is working, but its agent can't take the message any more (its turn has just
    /// ended, or messages are already held): held, and the session continues with it once its agent has ended.
    /// </summary>
    public sealed record Held : MessageOutcome;

    public sealed record NotFound : MessageOutcome;

    /// <summary>The session ended before its agent ever started: there's nothing to continue.</summary>
    public sealed record NeverStarted(SessionRecord Session) : MessageOutcome;

    /// <summary>The session's provider isn't configured on this server any more: it can't continue.</summary>
    public sealed record UnknownProvider(SessionRecord Session) : MessageOutcome;
}
