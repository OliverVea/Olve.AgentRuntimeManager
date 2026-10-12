namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>One line of a session's activity as the API returns it.</summary>
public sealed record ActivityLineBody(string Kind, string Text);
