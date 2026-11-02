using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary><c>GET /api/mcp-servers</c>: every registered MCP server.</summary>
public sealed class ListMcpServersHandler(IMcpServerStore store) : IMcpServersListHandler
{
    public Task<McpServersListResponse> HandleAsync(McpServersListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<McpServersListResponse>(new McpServersListResponse.Ok([.. store.List().Select(s => s.ToDto())]));
}
