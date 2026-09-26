namespace Olve.AgentRuntimeManager.Events;

/// <summary>Settings of the event stream (configuration section <c>Events</c>).</summary>
public sealed class EventOptions
{
    public const string Section = "Events";

    /// <summary>How often each connection gets a heartbeat (SPEC: 30s). Also sent on connect.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many recent events are kept for <c>Last-Event-ID</c> replay; also how far a connection
    /// may fall behind before it's disconnected (to resume from the buffer).
    /// </summary>
    public int ReplayCapacity { get; set; } = 1000;
}
