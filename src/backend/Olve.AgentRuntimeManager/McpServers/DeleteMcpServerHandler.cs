using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary><c>DELETE /api/mcp-servers/{name}</c>: unregisters an MCP server (sessions that took it keep their definition).</summary>
public sealed class DeleteMcpServerHandler(IMcpServerStore store) : IMcpServersDeleteHandler
{
    public Task<McpServersDeleteResponse> HandleAsync(McpServersDeleteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<McpServersDeleteResponse>(
            store.Delete(request.Name)
                ? new McpServersDeleteResponse.NoContent()
                : new McpServersDeleteResponse.NotFound(McpServerErrors.NotFound(request.Name)));
}
