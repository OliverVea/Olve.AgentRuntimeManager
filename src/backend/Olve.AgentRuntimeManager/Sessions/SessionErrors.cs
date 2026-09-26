using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>The session API's error codes (stable once published, docs/STANDARDS.md).</summary>
public static class SessionErrors
{
    public static ArmError NotFound(Guid id) =>
        ArmError.Create("SESSION_NOT_FOUND", $"Session {id} was not found.");

    public static ArmError AlreadyEnded(SessionRecord session) =>
        ArmError.Create("SESSION_ALREADY_ENDED", $"Session {session.Id} has already ended ({Wire(session.Status)}).");

    public static ArmError NotEnded(SessionRecord session) =>
        ArmError.Create("SESSION_NOT_ENDED", $"Session {session.Id} is {Wire(session.Status)}; only a session that has ended can be deleted.");

    public static ArmError QueueFull(int maxQueueSize) =>
        ArmError.Create("QUEUE_FULL", $"The queue is full ({maxQueueSize} sessions are waiting); try again later.");

    public static ArmError UnknownProvider(string provider, IReadOnlyList<string> known) =>
        ArmError.Create("UNKNOWN_PROVIDER", $"No provider '{provider}'. Known providers: {string.Join(", ", known)}.");

    public static ArmError BlankPrompt() =>
        ArmError.Create(ArmErrors.InvalidRequest, "'prompt' cannot be blank.");

    private static string Wire(SessionStatus status) => status.ToString().ToLowerInvariant();
}
