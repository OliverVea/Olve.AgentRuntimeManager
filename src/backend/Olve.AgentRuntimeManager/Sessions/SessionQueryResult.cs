namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>One page of <see cref="SessionManager.Search"/>: the sessions and the total matched.</summary>
public sealed record SessionQueryResult(IReadOnlyList<SessionRecord> Items, int Total, int Limit, int Offset);
