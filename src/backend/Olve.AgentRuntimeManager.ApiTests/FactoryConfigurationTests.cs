using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>Guards the in-process host's configuration: test auth wins, deployed settings don't leak in.</summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class FactoryConfigurationTests(ApiTarget target)
{
    [Test]
    public async Task App_UsesTestSigningKey()
    {
        var config = target.Factory.Services.GetRequiredService<IConfiguration>();

        await Assert.That(config["Auth:SigningKey"]).IsEqualTo(ApiFactory.Tokens.SigningKey);
    }

    [Test]
    public async Task App_HasTelemetryExportDisabled()
    {
        var services = target.Factory.Services;

        await Assert.That(services.GetRequiredService<IConfiguration>()["OpenTelemetry:Endpoint"]).IsNull();
        await Assert.That(services.GetService<TracerProvider>()).IsNull();
    }
}
