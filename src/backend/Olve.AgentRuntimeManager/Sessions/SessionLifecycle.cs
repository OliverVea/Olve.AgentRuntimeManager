using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// The session state machine: <c>queued → working → completed</c>; <c>queued → cancelled</c>
/// (withdrawn before it started); <c>working → killed</c>; <c>queued|working → failed</c>.
/// Terminal: completed, cancelled, killed, failed. (SPEC's <c>waiting</c>
/// state arrives with approvals, M8.)
/// </summary>
public static class SessionLifecycle
{
    private static readonly IReadOnlyDictionary<SessionStatus, SessionStatus[]> Next = new Dictionary<SessionStatus, SessionStatus[]>
    {
        [SessionStatus.Queued] = [SessionStatus.Working, SessionStatus.Cancelled, SessionStatus.Failed],
        [SessionStatus.Working] = [SessionStatus.Completed, SessionStatus.Killed, SessionStatus.Failed],
        [SessionStatus.Completed] = [],
        [SessionStatus.Cancelled] = [],
        [SessionStatus.Killed] = [],
        [SessionStatus.Failed] = [],
    };

    public static bool IsTerminal(SessionStatus status) => Next[status].Length == 0;

    public static bool CanMove(SessionStatus from, SessionStatus to) => Next[from].Contains(to);

    /// <summary>Throws on a move the state machine doesn't allow: a bug, never an expected failure.</summary>
    public static SessionStatus Move(SessionStatus from, SessionStatus to) =>
        CanMove(from, to) ? to : throw new InvalidOperationException($"A session can't move from {from} to {to}.");
}
