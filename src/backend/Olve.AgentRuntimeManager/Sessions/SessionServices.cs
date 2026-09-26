using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>The session runtime, its providers and the <c>Sessions_*</c> and <c>ProvidersHealth_*</c> handlers.</summary>
public static class SessionServices
{
    public static void AddSessionServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SessionOptions>(configuration.GetSection(SessionOptions.Section));
        services.Configure<FakeProviderOptions>(configuration.GetSection(FakeProviderOptions.Section));
        services.Configure<ClaudeProviderOptions>(configuration.GetSection(ClaudeProviderOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        if (configuration.GetSection(FakeProviderOptions.Section).Get<FakeProviderOptions>()?.Enabled ?? true)
        {
            services.AddSingleton<IAgentProvider, FakeProvider>();
        }

        services.AddSingleton<IAgentProvider, ClaudeProvider>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<IdempotencyStore<SessionsCreateResponse>>();

        services.AddSingleton<ISessionsCreateHandler, CreateSessionHandler>();
        services.AddSingleton<ISessionsSearchHandler, SearchSessionsHandler>();
        services.AddSingleton<ISessionsGetHandler, GetSessionHandler>();
        services.AddSingleton<ISessionsKillHandler, KillSessionHandler>();
        services.AddSingleton<ISessionsDeleteHandler, DeleteSessionHandler>();
        services.AddSingleton<IProvidersHealthListHandler, ListProviderHealthHandler>();
    }
}
