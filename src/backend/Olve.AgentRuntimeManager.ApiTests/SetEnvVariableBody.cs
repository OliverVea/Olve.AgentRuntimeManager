namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A <c>PUT /api/env/{name}</c> body.</summary>
public sealed record SetEnvVariableBody(string Value, bool Default);
