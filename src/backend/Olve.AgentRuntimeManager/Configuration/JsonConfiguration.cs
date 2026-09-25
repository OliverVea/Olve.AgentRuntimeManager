using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Olve.MinimalApi;
using Olve.Utilities.Ids;

namespace Olve.AgentRuntimeManager.Configuration;

public static class JsonConfiguration
{
    public static void ConfigureJson(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
        builder.Services.WithPathJsonConversion();
        builder.Services.AddOpenApi(options => options.AddSchemaTransformer(MapIdTypesToString));
    }

    // Id<T> (and Id) serialize as a GUID string at runtime, but OpenAPI introspects them as opaque
    // objects. Map them to a string schema so generated clients get `string`, not an empty type.
    private static Task MapIdTypesToString(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(Id) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Id<>)))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "uuid";
            schema.Properties?.Clear();
        }

        return Task.CompletedTask;
    }

    public static void MapJson(this WebApplication app)
    {
        app.MapOpenApi();
    }
}
