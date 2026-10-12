namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A session's activity as the API returns it.</summary>
public sealed record ActivityBody(ActivityLineBody[] Lines);
