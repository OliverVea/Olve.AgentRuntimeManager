using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// One provider's health as the <see cref="SessionManager"/> tracks it (in memory, under its lock):
/// whether its queued sessions may start, and why not.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>available</c>: sessions start.</item>
///   <item><c>limited</c>: none start until <see cref="Until"/>, then it's available again.</item>
///   <item><c>unreachable</c>: none start until <see cref="Until"/>; then one (the <see cref="Probe"/>)
///   tries, and its outcome decides: available again, or unreachable with a longer wait.</item>
///   <item><c>unauthorized</c>: none start until a restart (new credentials mean a redeploy).</item>
/// </list>
/// </remarks>
internal sealed class ProviderState(string name)
{
    public string Name => name;

    public ProviderStatus Status { get; private set; } = ProviderStatus.Available;

    public string? Reason { get; private set; }

    public DateTimeOffset? Since { get; private set; }

    public DateTimeOffset? Until { get; private set; }

    /// <summary>
    /// Bumped on every change. A run's outcome changes the health only if the run started in the
    /// current generation (or is the probe): when an outage hits, the other runs that started
    /// before it fail too, and must not stretch its wait; a run that started before a usage limit
    /// and succeeds must not lift it.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>Unreachable in a row, for the wait before the next try.</summary>
    public int Failures { get; private set; }

    /// <summary>The session trying an unreachable provider again, while it runs.</summary>
    public Guid? Probe { get; private set; }

    /// <summary>What wakes the provider up at <see cref="Until"/>.</summary>
    public ITimer? Timer { get; set; }

    /// <summary>
    /// Whether an unreachable provider's wait is over. Set by its timer, not read off the clock: a
    /// timer may fire a moment before <see cref="Until"/>, and nothing would wake it again.
    /// </summary>
    public bool WaitOver { get; private set; }

    public bool MayStart => Status switch
    {
        ProviderStatus.Available => true,
        ProviderStatus.Unreachable => Probe is null && WaitOver,
        _ => false,
    };

    public void EndWait() => WaitOver = true;

    /// <summary>A session started on it; on an unreachable provider, that's the probe.</summary>
    public void Started(Guid sessionId)
    {
        if (Status == ProviderStatus.Unreachable)
        {
            Probe = sessionId;
        }
    }

    /// <summary>A session's run ended; returns whether it was the probe (which then stops being it).</summary>
    public bool Ended(Guid sessionId)
    {
        if (Probe != sessionId)
        {
            return false;
        }

        Probe = null;
        return true;
    }

    public void Enter(ProviderStatus status, string reason, DateTimeOffset now, DateTimeOffset? until)
    {
        Failures = status == ProviderStatus.Unreachable ? (Status == ProviderStatus.Unreachable ? Failures + 1 : 1) : 0;
        Status = status;
        Reason = reason;
        Since = now;
        Until = until;
        Probe = null;
        WaitOver = false;
        Generation++;
    }

    public void Recover()
    {
        Status = ProviderStatus.Available;
        Reason = null;
        Since = null;
        Until = null;
        Failures = 0;
        Probe = null;
        WaitOver = false;
        Generation++;
    }

    public ProviderHealth ToDto() => new()
    {
        Provider = Name,
        Status = Status,
        Reason = Reason,
        Since = Since,
        Until = Until,
    };
}
