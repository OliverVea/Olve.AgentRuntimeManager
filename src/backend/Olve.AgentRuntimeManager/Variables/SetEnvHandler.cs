using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary><c>PUT /api/env/{name}</c>: registers an environment variable, or replaces it.</summary>
public sealed class SetEnvHandler(IEnvStore store, TimeProvider time) : IEnvSetHandler
{
    public Task<EnvSetResponse> HandleAsync(EnvSetRequest request, CancellationToken cancellationToken)
    {
        if (EnvErrors.Check(request.Name) is { } problem)
        {
            return Task.FromResult<EnvSetResponse>(new EnvSetResponse.BadRequest(problem));
        }

        var variable = new EnvVariableRecord
        {
            Name = request.Name, Value = request.Body.Value, IsDefault = request.Body.Default, UpdatedAt = time.GetUtcNow(),
        };
        store.Set(variable);
        return Task.FromResult<EnvSetResponse>(new EnvSetResponse.Ok(variable.ToDto()));
    }
}
