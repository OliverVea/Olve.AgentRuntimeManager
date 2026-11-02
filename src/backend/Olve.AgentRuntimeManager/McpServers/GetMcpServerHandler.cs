using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary><c>GET /api/mcp-servers/{name}</c>: one registered MCP server (a name no server can have is a 404 too).</summary>
public sealed class GetMcpServerHandler(IMcpServerStore store) : IMcpServersGetHandler
{
    public Task<McpServersGetResponse> HandleAsync(McpServersGetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<McpServersGetResponse>(
            store.Find(request.Name) is { } server
                ? new McpServersGetResponse.Ok(server.ToDto())
                : new McpServersGetResponse.NotFound(McpServerErrors.NotFound(request.Name)));
}
