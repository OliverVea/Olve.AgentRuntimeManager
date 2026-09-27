using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// A session in a slot: its agent, the timer that kills it if it has a timeout, the
/// <see cref="ProviderState.Generation"/> its provider was in when it started, and the messages
/// its agent was given (<paramref name="Given"/>, at its start and since), which go back to the
/// session if its provider refuses the agent.
/// </summary>
internal sealed record RunningAgent(IAgentRun Run, ITimer? Timeout, int Generation, List<string> Given);
