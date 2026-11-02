using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>Domain → contract.</summary>
public static class McpServerMapping
{
    public static McpServer ToDto(this McpServerRecord server) => new()
    {
        Name = server.Name,
        Description = server.Description,
        Command = server.Command,
        Args = server.Args,
        Env = server.Env,
        UpdatedAt = server.UpdatedAt,
    };
}
