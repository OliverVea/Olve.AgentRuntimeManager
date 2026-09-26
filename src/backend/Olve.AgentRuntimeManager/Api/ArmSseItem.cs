namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// One event an SSE operation's handler yields: its data and its SSE id (<c>null</c> sends it
/// without one, e.g. heartbeats, so clients' <c>Last-Event-ID</c> never points at it).
/// </summary>
public readonly record struct ArmSseItem<T>(T Data, string? Id = null);
