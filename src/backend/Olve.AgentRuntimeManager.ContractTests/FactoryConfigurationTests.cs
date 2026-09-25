using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Olve.AgentRuntimeManager.ContractTests;

/// <summary>Guards the test host's configuration: test auth wins, deployed settings don't leak in.</summary>
[ClassDataSource<ApiFactory>(Shared = SharedType.PerAssembly)]
public class FactoryConfigurationTests(ApiFactory factory)
{
    [Test]
    public async Task App_UsesTestSigningKey()
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();

        await Assert.That(config["Auth:SigningKey"]).IsEqualTo(ApiFactory.SigningKey);
    }

    [Test]
    public async Task App_HasTelemetryExportDisabled()
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();

        await Assert.That(config["OpenTelemetry:Endpoint"]).IsNull();
        await Assert.That(factory.Services.GetService<TracerProvider>()).IsNull();
    }
}
