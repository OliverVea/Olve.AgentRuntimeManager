using Olve.Utilities.Ids;
using Olve.Utilities.Lookup;

namespace Olve.AgentRuntimeManager.Messages;

/// <summary>
/// A persisted message — the template's one end-to-end CRUD example. Implementing
/// <see cref="IHasId{TId}"/> with an <see cref="Id{T}"/> is what lets it live in an
/// <c>EntityStore&lt;Message&gt;</c>.
/// </summary>
public sealed record Message(Id<Message> Id, string Text) : IHasId<Id<Message>>;
