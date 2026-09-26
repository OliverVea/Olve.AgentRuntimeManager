namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>How an agent ended.</summary>
public abstract record AgentOutcome
{
    private AgentOutcome()
    {
    }

    /// <summary>The agent finished on its own.</summary>
    public sealed record Completed(int ExitCode) : AgentOutcome;

    /// <summary>The agent crashed or its provider gave up.</summary>
    public sealed record Failed(string Error) : AgentOutcome;

    /// <summary>
    /// The provider failed before the agent did anything, so the session can run again later.
    /// <paramref name="Until"/>: when the provider says it's back (a usage limit's reset), if it says.
    /// </summary>
    public sealed record Unavailable(ProviderTrouble Trouble, string Error, DateTimeOffset? Until = null) : AgentOutcome;

    /// <summary>The agent was stopped (<see cref="IAgentRun.Kill"/>, or from outside ARM).</summary>
    public sealed record Killed : AgentOutcome;
}
