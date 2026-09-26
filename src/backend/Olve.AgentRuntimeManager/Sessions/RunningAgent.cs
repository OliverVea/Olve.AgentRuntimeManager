using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>A session in a slot: its agent and, if it has a timeout, the timer that kills it then.</summary>
internal sealed record RunningAgent(IAgentRun Run, ITimer? Timeout);
