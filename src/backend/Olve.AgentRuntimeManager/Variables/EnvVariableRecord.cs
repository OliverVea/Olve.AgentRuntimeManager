namespace Olve.AgentRuntimeManager.Variables;

/// <summary>An environment variable registered for agents (<c>/api/env</c>).</summary>
public sealed record EnvVariableRecord
{
    public required string Name { get; init; }
    public required string Value { get; init; }

    /// <summary>Whether every agent gets it; otherwise only sessions that name it in <c>useEnv</c>.</summary>
    public required bool IsDefault { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
