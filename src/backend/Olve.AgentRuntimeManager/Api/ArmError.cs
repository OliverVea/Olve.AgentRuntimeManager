using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>One error: a stable <c>SCREAMING_SNAKE_CASE</c> code, a readable message and details.</summary>
public sealed record ArmError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] ArmErrorDetails Details)
{
    /// <summary>An error without details.</summary>
    public static ArmError Create(string code, string message) => new(code, message, new ArmErrorDetails());
}
