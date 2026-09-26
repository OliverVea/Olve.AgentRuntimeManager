using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>One problem of several (<see cref="ArmErrorDetails.Problems"/>).</summary>
public sealed record ArmErrorProblem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
