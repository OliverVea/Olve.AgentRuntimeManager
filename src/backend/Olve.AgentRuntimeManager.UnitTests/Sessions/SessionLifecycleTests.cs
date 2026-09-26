using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

public class SessionLifecycleTests
{
    // SPEC §Sessions: queued → working → completed; working ⇄ waiting; queued|working|waiting → killed;
    // queued|working → failed.
    private static readonly HashSet<(SessionStatus, SessionStatus)> Allowed =
    [
        (SessionStatus.Queued, SessionStatus.Working),
        (SessionStatus.Working, SessionStatus.Completed),
        (SessionStatus.Working, SessionStatus.Waiting),
        (SessionStatus.Waiting, SessionStatus.Working),
        (SessionStatus.Queued, SessionStatus.Killed),
        (SessionStatus.Working, SessionStatus.Killed),
        (SessionStatus.Waiting, SessionStatus.Killed),
        (SessionStatus.Queued, SessionStatus.Failed),
        (SessionStatus.Working, SessionStatus.Failed),
    ];

    public static IEnumerable<Func<(SessionStatus From, SessionStatus To)>> AllMoves() =>
        from source in Enum.GetValues<SessionStatus>()
        from target in Enum.GetValues<SessionStatus>()
        select (Func<(SessionStatus, SessionStatus)>)(() => (source, target));

    [Test]
    [MethodDataSource(nameof(AllMoves))]
    public async Task CanMove_FollowsTheSpec(SessionStatus from, SessionStatus to) =>
        await Assert.That(SessionLifecycle.CanMove(from, to)).IsEqualTo(Allowed.Contains((from, to)));

    [Test]
    [Arguments(SessionStatus.Completed, true)]
    [Arguments(SessionStatus.Killed, true)]
    [Arguments(SessionStatus.Failed, true)]
    [Arguments(SessionStatus.Queued, false)]
    [Arguments(SessionStatus.Working, false)]
    [Arguments(SessionStatus.Waiting, false)]
    public async Task IsTerminal_IsCompletedKilledOrFailed(SessionStatus status, bool terminal) =>
        await Assert.That(SessionLifecycle.IsTerminal(status)).IsEqualTo(terminal);

    [Test]
    public async Task Move_ThrowsOnAMoveTheSpecForbids() =>
        await Assert.That(() => SessionLifecycle.Move(SessionStatus.Completed, SessionStatus.Working)).Throws<InvalidOperationException>();
}
