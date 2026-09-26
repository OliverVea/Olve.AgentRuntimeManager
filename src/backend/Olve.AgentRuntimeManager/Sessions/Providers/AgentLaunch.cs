namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// What a provider needs to start a session's agent. <paramref name="Attempt"/> counts from 1;
/// <paramref name="ProviderSessionId"/> is the id to give the agent, new for every attempt (a
/// failed attempt's id stays taken).
/// </summary>
public sealed record AgentLaunch(Guid SessionId, string Prompt, string Model, int Attempt, Guid ProviderSessionId);
