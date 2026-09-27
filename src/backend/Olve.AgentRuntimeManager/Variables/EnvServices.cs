using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary>The <c>Env_*</c> handlers (the store is registered with the persistence).</summary>
public static class EnvServices
{
    public static void AddEnvServices(this IServiceCollection services)
    {
        services.AddSingleton<IEnvListHandler, ListEnvHandler>();
        services.AddSingleton<IEnvSetHandler, SetEnvHandler>();
        services.AddSingleton<IEnvDeleteHandler, DeleteEnvHandler>();
    }
}
