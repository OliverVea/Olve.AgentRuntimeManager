using System.Text.Json.Serialization;
using Olve.Results;
using Olve.AgentRuntimeManager.Messages;
using Olve.Utilities.Ids;

namespace Olve.AgentRuntimeManager;

// Source-generated JSON for the hand-written types. The API's DTOs live in the generated
// ArmJsonContext (artifacts/generated/backend); this covers the rest:
// - ResultProblem[]: the 400 body Olve.MinimalApi's WithValidation filter writes;
// - the domain Message list: the snapshot the EntityStore persister writes. Id<Message> is
//   registered explicitly so its closed generic is statically reachable here (the Id<T>
//   converter is reflection-based).
[JsonSerializable(typeof(ResultProblem[]))]
[JsonSerializable(typeof(IReadOnlyList<Message>))]
[JsonSerializable(typeof(Id<Message>))]
internal partial class AppJsonContext : JsonSerializerContext;
