using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Events;

/// <summary>The event stream (<c>GET /api/events</c>): the bus, its settings and the handler.</summary>
public static class EventServices
{
    public static void AddEventServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EventOptions>(configuration.GetSection(EventOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventsStreamHandler, StreamEventsHandler>();
    }
}
