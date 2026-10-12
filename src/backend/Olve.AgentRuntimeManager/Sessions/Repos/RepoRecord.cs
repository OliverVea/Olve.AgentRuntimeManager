namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>
/// A git repository a session names (M5e): its URL and, if given, the branch, tag or commit to
/// start from; and the folder and branch it is checked out as, named once when the session is
/// created (<see cref="RepoNames.Record"/>). The checkout uses these, and the API returns them.
/// </summary>
public sealed record RepoRecord(string Url, string? Ref, string Folder, string Branch);
