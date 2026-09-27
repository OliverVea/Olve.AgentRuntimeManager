using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary><c>DELETE /api/env/{name}</c>: unregisters an environment variable (sessions that took it keep their value).</summary>
public sealed class DeleteEnvHandler(IEnvStore store) : IEnvDeleteHandler
{
    public Task<EnvDeleteResponse> HandleAsync(EnvDeleteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<EnvDeleteResponse>(
            !EnvNames.IsValid(request.Name) ? new EnvDeleteResponse.BadRequest(EnvErrors.InvalidName(request.Name))
            : store.Delete(request.Name) ? new EnvDeleteResponse.NoContent()
            : new EnvDeleteResponse.NotFound(EnvErrors.NotFound(request.Name)));
}
