namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>An error's details: every problem, when there were several.</summary>
public sealed record ErrorDetailsBody(List<ErrorProblemBody>? Problems);
