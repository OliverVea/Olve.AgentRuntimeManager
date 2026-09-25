namespace Olve.AgentRuntimeManager.Health;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok())
            .AllowAnonymous();
    }
}
