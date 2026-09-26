namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A page of <c>POST /api/sessions/search</c>.</summary>
public sealed record SessionPageBody(List<SessionBody> Items, int Total, int Limit, int Offset);
