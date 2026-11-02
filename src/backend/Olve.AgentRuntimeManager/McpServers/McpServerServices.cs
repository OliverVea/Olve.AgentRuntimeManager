using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>The <c>McpServers_*</c> handlers (the store is registered with the persistence).</summary>
public static class McpServerServices
{
    public static void AddMcpServerServices(this IServiceCollection services)
    {
        services.AddSingleton<IMcpServersListHandler, ListMcpServersHandler>();
        services.AddSingleton<IMcpServersGetHandler, GetMcpServerHandler>();
        services.AddSingleton<IMcpServersSetHandler, SetMcpServerHandler>();
        services.AddSingleton<IMcpServersDeleteHandler, DeleteMcpServerHandler>();
    }
}
