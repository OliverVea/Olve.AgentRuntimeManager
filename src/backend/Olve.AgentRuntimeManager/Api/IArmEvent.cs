namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// An event of a generated SSE event union (an <c>@events</c> union in the contract). The event
/// name on the wire is its <see cref="EventType"/>, which is also the <c>type</c> of its data.
/// </summary>
public interface IArmEvent
{
    /// <summary>The SSE event name, e.g. <c>session.created</c>.</summary>
    string EventType { get; }
}
