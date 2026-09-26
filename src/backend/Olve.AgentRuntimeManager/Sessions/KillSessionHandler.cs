using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>POST /api/sessions/{id}/kill</c>: cancels a queued session, kills a working one.</summary>
public sealed class KillSessionHandler(SessionManager sessions) : ISessionsKillHandler
{
    public Task<SessionsKillResponse> HandleAsync(SessionsKillRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SessionsKillResponse>(sessions.Kill(request.Id, request.Body.Reason, KillSource.User, request.Body.Caller) switch
        {
            KillOutcome.Stopped stopped => stopped.Session.ToDto(),
            KillOutcome.AlreadyEnded ended => new SessionsKillResponse.Conflict(SessionErrors.AlreadyEnded(ended.Session)),
            KillOutcome.NotFound => new SessionsKillResponse.NotFound(SessionErrors.NotFound(request.Id)),
            _ => throw new InvalidOperationException("Unhandled kill outcome."),
        });
}
