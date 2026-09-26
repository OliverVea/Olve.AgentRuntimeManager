using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>GET /api/providers/health</c>: every provider's health, as the session runtime tracks it.</summary>
public sealed class ListProviderHealthHandler(SessionManager sessions) : IProvidersHealthListHandler
{
    public Task<ProvidersHealthListResponse> HandleAsync(ProvidersHealthListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ProvidersHealthListResponse>(new ProvidersHealthListResponse.Ok(sessions.Health()));
}
