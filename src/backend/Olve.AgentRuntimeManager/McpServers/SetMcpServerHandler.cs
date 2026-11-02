using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>
/// <c>PUT /api/mcp-servers/{name}</c>: registers an MCP server, or replaces it. Doesn't start it,
/// and sessions that took the old definition keep it.
/// </summary>
public sealed class SetMcpServerHandler(IMcpServerStore store, TimeProvider time) : IMcpServersSetHandler
{
    public Task<McpServersSetResponse> HandleAsync(McpServersSetRequest request, CancellationToken cancellationToken)
    {
        var env = request.Body.Env ?? new Dictionary<string, string>();
        if ((McpServerErrors.Check(request.Name) ?? env.Keys.Select(EnvErrors.Check).FirstOrDefault(p => p is not null)) is { } problem)
        {
            return Task.FromResult<McpServersSetResponse>(new McpServersSetResponse.BadRequest(problem));
        }

        var server = new McpServerRecord
        {
            Name = request.Name,
            Description = request.Body.Description,
            Command = request.Body.Command,
            Args = request.Body.Args ?? [],
            Env = env,
            UpdatedAt = time.GetUtcNow(),
        };
        store.Set(server);
        return Task.FromResult<McpServersSetResponse>(new McpServersSetResponse.Ok(server.ToDto()));
    }
}
