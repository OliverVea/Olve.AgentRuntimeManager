using Microsoft.AspNetCore.Http.Json;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>Wires the generated API surface (<c>artifacts/generated/backend</c>) into the app.</summary>
public static class ArmApiExtensions
{
    public static void AddArmApi(this IServiceCollection services)
    {
        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArmJsonContext.Default);
            // Discriminated unions: the discriminator may appear anywhere in a request object.
            options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
        });

        // Surface binding failures as BadHttpRequestException so ArmBindingFailures can answer
        // them with the contract's error body.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
    }

    /// <summary>The generated handler interfaces that have no registered implementation.</summary>
    public static IReadOnlyList<Type> MissingHandlers(this IServiceProvider services)
    {
        var isService = services.GetRequiredService<IServiceProviderIsService>();
        return [.. ArmApi.HandlerTypes.Where(type => !isService.IsService(type))];
    }

    /// <summary>
    /// Maps every contract operation. Fails fast if any operation's handler isn't registered, so a
    /// new operation in the spec can't ship as a runtime 500.
    /// </summary>
    public static ArmEndpoints UseArmApi(this WebApplication app)
    {
        var missing = app.Services.MissingHandlers();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "No handler registered for: " + string.Join(", ", missing.Select(type => type.Name)));
        }

        app.UseMiddleware<ArmBindingFailures>();
        return ((IEndpointRouteBuilder)app).MapArmApi();
    }
}
