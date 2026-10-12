using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// A session in a slot: its agent, the timer that kills it if it has a timeout, the
/// <see cref="ProviderState.Generation"/> its provider was in when it started, and the messages
/// its agent was given (<paramref name="Given"/>, at its start and since), which go back to the
/// session if its provider refuses the agent. <paramref name="PreviousProviderSessionId"/>: the
/// session's provider session before this run, which it gets back if this run's agent never starts
/// (M5e: a checkout that failed or was killed), so it isn't resumed later as if it had.
/// </summary>
internal sealed record RunningAgent(IAgentRun Run, ITimer? Timeout, int Generation, List<string> Given, string? PreviousProviderSessionId);
