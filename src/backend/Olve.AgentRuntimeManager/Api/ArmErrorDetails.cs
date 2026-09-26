using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>Details of an error: every problem found, when there was more than one.</summary>
public sealed record ArmErrorDetails
{
    [JsonPropertyName("problems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ArmErrorProblem>? Problems { get; init; }
}
