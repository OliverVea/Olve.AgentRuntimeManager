namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// Runs agents for one provider (<c>fake</c>, later <c>claude</c>, <c>codex</c>): the seam between
/// the session runtime and whatever actually executes an agent (OPEN-QUESTIONS A6).
/// </summary>
public interface IAgentProvider
{
    /// <summary>The name sessions select it by (<c>provider</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Starts an agent and returns at once; the run completes on its own. Throws when the agent
    /// can't start (the session then fails). Must not block: it's called while the runtime holds
    /// its lock.
    /// </summary>
    IAgentRun Start(AgentLaunch launch);
}
