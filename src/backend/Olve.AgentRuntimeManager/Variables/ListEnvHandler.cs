using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary><c>GET /api/env</c>: every registered environment variable.</summary>
public sealed class ListEnvHandler(IEnvStore store) : IEnvListHandler
{
    public Task<EnvListResponse> HandleAsync(EnvListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<EnvListResponse>(new EnvListResponse.Ok([.. store.List().Select(v => v.ToDto())]));
}
