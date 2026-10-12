namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A git repository of a session (M5e) as returned: as given, with the folder and branch ARM gave it.</summary>
public sealed record SessionRepoBody(string Url, string? Ref, string Folder, string Branch);
