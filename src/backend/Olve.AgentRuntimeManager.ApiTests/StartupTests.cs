using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// ARM registers a handler for every generated operation (UseArmApi would refuse to start
/// otherwise; this names the gap in a test instead of a crashed host).
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class StartupTests(ApiTarget target)
{
    [Test]
    public async Task EveryGeneratedHandler_IsRegistered()
    {
        var missing = target.Factory.Services.MissingHandlers();

        await Assert.That(ArmApi.HandlerTypes).IsNotEmpty();
        await Assert.That(missing.Select(t => t.Name).ToList()).IsEmpty();
    }
}
