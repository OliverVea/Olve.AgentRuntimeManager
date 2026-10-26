using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Activities;
using Olve.AgentRuntimeManager.Sessions.Conversations;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.Utilities.AsyncOnStartup;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>The session runtime, its providers and the <c>Sessions_*</c>, <c>SessionMessages_*</c>, <c>Providers_*</c> and <c>ProvidersHealth_*</c> handlers.</summary>
public static class SessionServices
{
    public static void AddSessionServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SessionOptions>(configuration.GetSection(SessionOptions.Section));
        services.AddOptions<FakeProviderOptions>().Bind(configuration.GetSection(FakeProviderOptions.Section))
            .Validate(o => ModelOptions.AreValid(o.Models), ModelOptions.Invalid(FakeProviderOptions.Section)).ValidateOnStart();
        services.AddOptions<ClaudeProviderOptions>().Bind(configuration.GetSection(ClaudeProviderOptions.Section))
            .Validate(o => ModelOptions.AreValid(o.Models), ModelOptions.Invalid(ClaudeProviderOptions.Section)).ValidateOnStart();
        services.Configure<SupervisorOptions>(configuration.GetSection(SupervisorOptions.Section));
        services.Configure<RepoOptions>(configuration.GetSection(RepoOptions.Section));
        services.AddSingleton<Supervisors>();
        services.AddSingleton<RepoCheckouts>();
        services.TryAddSingleton(TimeProvider.System);
        if (configuration.GetSection(FakeProviderOptions.Section).Get<FakeProviderOptions>()?.Enabled ?? true)
        {
            services.AddSingleton<IAgentProvider, FakeProvider>();
        }

        services.AddSingleton<IAgentProvider, ClaudeProvider>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<IAsyncOnStartup, ProviderCheck>();
        services.AddHostedService<WorktreeRemovalRetries>();
        services.AddSingleton<IdempotencyStore<SessionsCreateResponse>>();

        services.AddSingleton<ISessionsCreateHandler, CreateSessionHandler>();
        services.AddSingleton<ISessionsSearchHandler, SearchSessionsHandler>();
        services.AddSingleton<ISessionsGetHandler, GetSessionHandler>();
        services.AddSingleton<ISessionsKillHandler, KillSessionHandler>();
        services.AddSingleton<ISessionsDeleteHandler, DeleteSessionHandler>();
        services.AddSingleton<ISessionMessagesSendHandler, SendMessageHandler>();
        services.AddSingleton<IProvidersListHandler, ListProvidersHandler>();
        services.AddSingleton<IToolsListHandler, ListToolsHandler>();
        services.AddSingleton<IProvidersHealthListHandler, ListProviderHealthHandler>();
        services.AddSingleton<ISessionConversationGetHandler, GetConversationHandler>();
        services.AddSingleton<ISessionActivityGetHandler, GetActivityHandler>();
    }
}
