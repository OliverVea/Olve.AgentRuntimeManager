using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// <c>GET /api/providers</c>: every provider this server has, by name, with its models in their
/// configured order; a display name the configuration leaves blank is the name (or id).
/// </summary>
public sealed class ListProvidersHandler(IEnumerable<IAgentProvider> providers) : IProvidersListHandler
{
    public Task<ProvidersListResponse> HandleAsync(ProvidersListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ProvidersListResponse>(new ProvidersListResponse.Ok(
            [.. providers.OrderBy(p => p.Name, StringComparer.Ordinal).Select(ToDto)]));

    private static Provider ToDto(IAgentProvider provider) => new()
    {
        Name = provider.Name,
        DisplayName = OrElse(provider.DisplayName, provider.Name),
        Models = [.. provider.Models.Select(m => new ProviderModel { Id = m.Id, DisplayName = OrElse(m.DisplayName, m.Id) })],
    };

    private static string OrElse(string? displayName, string fallback) =>
        string.IsNullOrWhiteSpace(displayName) ? fallback : displayName;
}
