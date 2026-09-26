using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Configuration;

public static class JsonConfiguration
{
    public static void ConfigureJson(this WebApplicationBuilder builder)
    {
        builder.Services.AddArmApi();
        builder.Services.AddOpenApi();
    }

    public static void MapJson(this WebApplication app)
    {
        app.MapOpenApi();
    }
}
