using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>GET /api/sessions/{id}</c>.</summary>
public sealed class GetSessionHandler(SessionManager sessions) : ISessionsGetHandler
{
    public Task<SessionsGetResponse> HandleAsync(SessionsGetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SessionsGetResponse>(sessions.Get(request.Id) is { } session
            ? session.ToDto()
            : new SessionsGetResponse.NotFound(SessionErrors.NotFound(request.Id)));
}
