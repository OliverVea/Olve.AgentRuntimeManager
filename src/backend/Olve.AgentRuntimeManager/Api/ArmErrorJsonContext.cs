using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>Source-generated JSON for the error envelope, independent of the generated contract context.</summary>
[JsonSerializable(typeof(ArmErrorEnvelope))]
internal sealed partial class ArmErrorJsonContext : JsonSerializerContext;
