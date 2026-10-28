using Olve.AgentRuntimeManager.Api;
using Olve.TypeSpec;

namespace Olve.AgentRuntimeManager.Configuration;

public static class JsonConfiguration
{
    public static void ConfigureJson(this WebApplicationBuilder builder)
    {
        builder.Services.AddArmApi();
        builder.Services.AddSingleton<IApiErrorWriter, ArmErrorWriter>();
        builder.Services.AddOpenApi();
    }

    public static void MapJson(this WebApplication app)
    {
        app.MapOpenApi();
    }
}
