using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>GET /api/tools</c>: ARM's tools (M10), in the catalog's order, each saying whether it's one of the server's defaults.</summary>
public sealed class ListToolsHandler(IOptions<ToolsOptions> options) : IToolsListHandler
{
    public Task<ToolsListResponse> HandleAsync(ToolsListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ToolsListResponse>(new ToolsListResponse.Ok(
            [.. ToolCatalog.All.Select(t => new Tool { Name = t.Name, Description = t.Description, Default = options.Value.Default.Contains(t.Name) })]));
}
