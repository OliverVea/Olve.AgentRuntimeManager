using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Configuration;

/// <summary>
/// <c>GET /api/server-info</c>: which build this server runs and where. Both come from the deploy
/// (<c>vm-deploy.sh</c> writes <c>Arm__Version</c> and <c>Arm__Environment</c>), so a local run
/// has neither.
/// </summary>
public sealed class GetServerInfoHandler(IConfiguration config) : IServerInfoApiGetHandler
{
    public Task<ServerInfoApiGetResponse> HandleAsync(ServerInfoApiGetRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult<ServerInfoApiGetResponse>(new ServerInfo
        {
            Version = NullIfEmpty(config["Arm:Version"]),
            Environment = NullIfEmpty(config["Arm:Environment"]),
        });
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
