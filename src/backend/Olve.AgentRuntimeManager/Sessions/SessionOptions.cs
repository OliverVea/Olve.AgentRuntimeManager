namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Settings of the session runtime (configuration section <c>Sessions</c>).</summary>
public sealed class SessionOptions
{
    public const string Section = "Sessions";

    /// <summary>Sessions that may run at once (SPEC <c>totalSlots</c>); more are queued.</summary>
    public int TotalSlots { get; set; } = 10;

    /// <summary>Sessions that may wait for a slot (SPEC <c>maxQueueSize</c>); more are a 503.</summary>
    public int MaxQueueSize { get; set; } = 200;

    /// <summary>How long an <c>Idempotency-Key</c> replays its original response (SPEC: 24 hours).</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromHours(24);
}
