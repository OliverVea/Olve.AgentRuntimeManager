namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A registered environment variable as the API returns it.</summary>
public sealed record EnvVariableBody(string Name, string Value, bool Default, DateTimeOffset UpdatedAt);
