namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>The <c>error</c> of an error envelope.</summary>
public sealed record ErrorBody(string Code, string Message, ErrorDetailsBody Details);
