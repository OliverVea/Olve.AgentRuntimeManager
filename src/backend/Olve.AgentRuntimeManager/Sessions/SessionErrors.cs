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

    public static ArmError BlankMessage() =>
        ArmError.Create(ArmErrors.InvalidRequest, "'text' cannot be blank.");

    public static ArmError NeverStarted(SessionRecord session) =>
        ArmError.Create("SESSION_NEVER_STARTED", $"Session {session.Id} was {Wire(session.Status)} before its agent ever started; there is no agent to continue.");

    public static ArmError ProviderGone(SessionRecord session) =>
        ArmError.Create("PROVIDER_GONE", $"Session {session.Id} ran on provider '{session.Provider}', which this server no longer has; it can't continue.");

    private static string Wire(SessionStatus status) => status.ToString().ToLowerInvariant();
}
