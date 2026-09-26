using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// A session in a slot: its agent, the timer that kills it if it has a timeout, and the
/// <see cref="ProviderState.Generation"/> its provider was in when it started.
/// </summary>
internal sealed record RunningAgent(IAgentRun Run, ITimer? Timeout, int Generation);
