using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>Source-generated JSON for everything the supervisor reads and writes (AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SupervisorLaunch))]
[JsonSerializable(typeof(SupervisorInfo))]
[JsonSerializable(typeof(SupervisorExit))]
[JsonSerializable(typeof(SupervisorMessage))]
public sealed partial class SupervisorJsonContext : JsonSerializerContext;
