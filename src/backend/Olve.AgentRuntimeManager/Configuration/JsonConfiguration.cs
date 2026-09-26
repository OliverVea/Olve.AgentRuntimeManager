using Olve.AgentRuntimeManager.Api;
using Olve.MinimalApi;

namespace Olve.AgentRuntimeManager.Configuration;

public static class JsonConfiguration
{
    public static void ConfigureJson(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
        builder.Services.AddArmApi();
        builder.Services.WithPathJsonConversion();
        builder.Services.AddOpenApi();
    }

    public static void MapJson(this WebApplication app)
    {
        app.MapOpenApi();
    }
}
