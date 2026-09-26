namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// Implements one contract operation: takes its generated request record and returns one of the
/// responses the contract declares for it (the generated per-operation response union), so an
/// undeclared status can't compile. Expected failures are declared responses, not exceptions.
/// </summary>
public interface IArmHandler<in TRequest, TResponse>
    where TResponse : IArmResponse
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
