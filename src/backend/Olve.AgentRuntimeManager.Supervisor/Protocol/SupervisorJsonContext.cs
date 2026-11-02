using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>Source-generated JSON for everything the supervisor reads and writes (AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SupervisorLaunch))]
[JsonSerializable(typeof(SupervisorRepo))]
[JsonSerializable(typeof(SupervisorSkill))]
[JsonSerializable(typeof(SupervisorSkillInstall))]
[JsonSerializable(typeof(SupervisorInfo))]
[JsonSerializable(typeof(SupervisorExit))]
[JsonSerializable(typeof(SupervisorMessage))]
[JsonSerializable(typeof(IReadOnlyList<McpServerDefinition>))]
[JsonSerializable(typeof(McpStartFailure))]
public sealed partial class SupervisorJsonContext : JsonSerializerContext;
