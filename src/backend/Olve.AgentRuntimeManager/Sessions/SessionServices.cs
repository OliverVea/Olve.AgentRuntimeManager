using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>The session runtime, its providers and the <c>Sessions_*</c> handlers.</summary>
public static class SessionServices
{
    public static void AddSessionServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SessionOptions>(configuration.GetSection(SessionOptions.Section));
        services.Configure<FakeProviderOptions>(configuration.GetSection(FakeProviderOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAgentProvider, FakeProvider>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<IdempotencyStore<SessionsCreateResponse>>();

        services.AddSingleton<ISessionsCreateHandler, CreateSessionHandler>();
        services.AddSingleton<ISessionsSearchHandler, SearchSessionsHandler>();
        services.AddSingleton<ISessionsGetHandler, GetSessionHandler>();
        services.AddSingleton<ISessionsKillHandler, KillSessionHandler>();
        services.AddSingleton<ISessionsDeleteHandler, DeleteSessionHandler>();
    }
}
