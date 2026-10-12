namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A git repository of a session (M5e), as sent and as returned.</summary>
public sealed record RepoBody(string Url, string? Ref = null);
