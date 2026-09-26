using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// The error body of every failed request (docs/STANDARDS.md):
/// <c>{ "error": { "code": "SESSION_NOT_FOUND", "message": "…", "details": {} } }</c>. The contract's
/// <c>ErrorEnvelope</c> model maps onto this type (<c>external-types</c> in the emitter config).
/// </summary>
public sealed record ArmErrorEnvelope([property: JsonPropertyName("error")] ArmError Error)
{
    /// <summary>Lets a handler answer an error response with just the <see cref="ArmError"/>.</summary>
    public static implicit operator ArmErrorEnvelope(ArmError error) => new(error);
}
