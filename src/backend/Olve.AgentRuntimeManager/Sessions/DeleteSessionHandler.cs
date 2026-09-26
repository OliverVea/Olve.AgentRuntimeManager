using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>DELETE /api/sessions/{id}</c>: deletes a session that has ended.</summary>
public sealed class DeleteSessionHandler(SessionManager sessions) : ISessionsDeleteHandler
{
    public Task<SessionsDeleteResponse> HandleAsync(SessionsDeleteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SessionsDeleteResponse>(sessions.Delete(request.Id) switch
        {
            DeleteOutcome.Deleted => new SessionsDeleteResponse.NoContent(),
            DeleteOutcome.NotEnded notEnded => new SessionsDeleteResponse.Conflict(SessionErrors.NotEnded(notEnded.Session)),
            DeleteOutcome.NotFound => new SessionsDeleteResponse.NotFound(SessionErrors.NotFound(request.Id)),
            _ => throw new InvalidOperationException("Unhandled delete outcome."),
        });
}
