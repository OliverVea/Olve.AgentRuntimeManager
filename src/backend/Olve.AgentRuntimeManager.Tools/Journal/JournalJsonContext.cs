using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Tools.Journal;

/// <summary>Source-generated JSON for the journal (Native AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
