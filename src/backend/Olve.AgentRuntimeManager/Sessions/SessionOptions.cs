namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Settings of the session runtime (configuration section <c>Sessions</c>).</summary>
public sealed class SessionOptions
{
    public const string Section = "Sessions";

    /// <summary>Sessions that may run at once (SPEC <c>totalSlots</c>); more are queued.</summary>
    public int TotalSlots { get; set; } = 10;

    /// <summary>Sessions that may wait for a slot (SPEC <c>maxQueueSize</c>); more are a 503.</summary>
    public int MaxQueueSize { get; set; } = 200;

    /// <summary>
    /// How often a session is retried after its provider refused it (outage, bad credentials; a
    /// wait for a known usage-limit reset doesn't count). Then it fails with the last error.
    /// </summary>
    public int ProviderRetries { get; set; } = 2;

    /// <summary>How long an unreachable provider waits before one queued session tries it again; doubles per failed try.</summary>
    public TimeSpan ProviderBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest <see cref="ProviderBackoff"/> gets.</summary>
    public TimeSpan ProviderMaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an <c>Idempotency-Key</c> replays its original response (SPEC: 24 hours).</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromHours(24);
}
