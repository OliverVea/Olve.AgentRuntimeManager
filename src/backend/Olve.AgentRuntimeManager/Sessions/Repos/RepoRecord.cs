namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>A git repository a session names (M5e): its URL and, if given, the branch, tag or commit to start from.</summary>
public sealed record RepoRecord(string Url, string? Ref = null);
