using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>A session in a slot: its agent and the timer that kills it at its timeout.</summary>
internal sealed record RunningAgent(IAgentRun Run, ITimer Timeout);
