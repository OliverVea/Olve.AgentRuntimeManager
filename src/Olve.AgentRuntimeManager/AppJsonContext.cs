using System.Text.Json.Serialization;
using Olve.Results;
using Olve.AgentRuntimeManager.Configuration;
using Olve.AgentRuntimeManager.Messages;
using Olve.Utilities.Ids;
using Olve.Utilities.Paginations;

namespace Olve.AgentRuntimeManager;

// Source-generated JSON for AOT. Id<Message> is registered explicitly so its closed generic is
// statically reachable here (the Id<T> converter is reflection-based — see docs/DESIGN.md §1.4a).
[JsonSerializable(typeof(ResultProblem[]))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(IReadOnlyList<Message>))]
[JsonSerializable(typeof(Page<Message>))]
[JsonSerializable(typeof(MessageRequest))]
[JsonSerializable(typeof(Id<Message>))]
// The Result<T> wrappers are the endpoint delegates' declared return types, which OpenAPI
// introspects at build time even though the result filter unwraps them at runtime.
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(Result<Message>))]
[JsonSerializable(typeof(Result<Page<Message>>))]
// Public OIDC config served to the SPA at GET /api/auth-config.
[JsonSerializable(typeof(FrontendAuthConfig))]
internal partial class AppJsonContext : JsonSerializerContext;
