using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.ContractTests;

/// <summary>
/// Checks on the generated API surface that the per-operation contract cases don't cover:
/// every generated handler interface is implemented, and binding failures on operations that
/// declare no 400 keep ASP.NET Core's bodyless 400.
/// </summary>
[ClassDataSource<ApiFactory>(Shared = SharedType.PerAssembly)]
public class GeneratedApiTests(ApiFactory factory)
{
    [Test]
    public async Task EveryGeneratedHandler_IsRegistered()
    {
        await Assert.That(ArmApi.HandlerTypes).IsNotEmpty();
        await Assert.That(factory.Services.MissingHandlers().Select(t => t.Name).ToList()).IsEmpty();
    }

    [Test]
    public async Task EveryContractOperation_HasAGeneratedOperation()
    {
        var generated = typeof(ArmOperations).GetFields()
            .Select(f => ((ArmOperation)f.GetValue(null)!).OperationId)
            .Order()
            .ToList();

        await Assert.That(generated).IsEquivalentTo(OpenApiContract.Operations.Select(o => o.OperationId).Order().ToList());
    }

    [Test]
    public async Task BindingFailure_OnOperationWithoutDeclared400_IsBodyless400()
    {
        using var response = await factory.CreateClient().GetAsync("/api/messages?pageSize=abc");

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEmpty();
    }
}
