using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Olve.AgentRuntimeManager.Configuration;

public static class TelemetryConfiguration
{
    public static void ConfigureTelemetry(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["OpenTelemetry:Endpoint"];
        if (endpoint is null) return;

        var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "olve-agentruntimemanager";
        var protocol = builder.Configuration["OpenTelemetry:Protocol"];

        var tokenUrl = builder.Configuration["OpenTelemetry:OAuth2:TokenUrl"];
        var clientId = builder.Configuration["OpenTelemetry:OAuth2:ClientId"];
        var clientSecret = builder.Configuration["OpenTelemetry:OAuth2:ClientSecret"];
        var scope = builder.Configuration["OpenTelemetry:OAuth2:Scope"];

        // OAuth2 is opt-in: only when all three are actually provided. Empty (not just null)
        // must disable it — otherwise an env var set to "" (e.g. a beta overlay that clears
        // TokenUrl/ClientId for an unauthenticated OTLP endpoint) would build a token provider
        // with blank credentials and crash the app at startup on the eager token request.
        OAuth2TokenProvider? tokenProvider = null;
        if (!string.IsNullOrEmpty(tokenUrl) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
        {
            tokenProvider = new OAuth2TokenProvider(tokenUrl, clientId, clientSecret, scope);
        }

        var baseEndpoint = endpoint.TrimEnd('/');

        void ConfigureOtlp(OtlpExporterOptions options, string signalPath)
        {
            options.Endpoint = new Uri($"{baseEndpoint}{signalPath}");

            if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase))
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            }

            if (tokenProvider is not null)
            {
                var token = tokenProvider.GetAccessToken();
                options.Headers = $"Authorization=Bearer {token}";
            }
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, "/v1/traces")))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, "/v1/metrics")));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.AddOtlpExporter(o => ConfigureOtlp(o, "/v1/logs"));
            logging.IncludeFormattedMessage = true;
        });
    }
}
