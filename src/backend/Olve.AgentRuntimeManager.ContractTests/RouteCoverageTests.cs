using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Olve.AgentRuntimeManager.ContractTests;

/// <summary>
/// Every operation in the contract is mapped by the app, and every <c>/api</c> endpoint the app
/// maps is in the contract (docs/STANDARDS.md: "every operation MUST be implemented, and every
/// /api endpoint MUST be in the contract").
/// </summary>
[ClassDataSource<ApiFactory>(Shared = SharedType.PerAssembly)]
public class RouteCoverageTests(ApiFactory factory)
{
    [Test]
    public async Task EverySpecOperation_IsMapped()
    {
        var spec = SpecRoutes();
        var mapped = MappedApiRoutes();

        await Assert.That(spec).IsNotEmpty();
        await Assert.That(spec.Except(mapped).Order().ToList()).IsEmpty();
    }

    [Test]
    public async Task EveryMappedApiEndpoint_IsInSpec()
    {
        var spec = SpecRoutes();
        var mapped = MappedApiRoutes();

        await Assert.That(mapped).IsNotEmpty();
        await Assert.That(mapped.Except(spec).Order().ToList()).IsEmpty();
    }

    [Test]
    [Arguments("/api/messages/{id}", "/api/messages/{}")]
    [Arguments("/api/messages/{id:guid}", "/api/messages/{}")]
    [Arguments("api/messages/{id?}/", "/api/messages/{}")]
    [Arguments("/API/Messages", "/api/messages")]
    public async Task NormalizePath_IgnoresParameterNamesAndConstraints(string template, string expected) =>
        await Assert.That(OpenApiContract.NormalizePath(template)).IsEqualTo(expected);

    private static HashSet<string> SpecRoutes() =>
        [.. OpenApiContract.Operations.Select(o => $"{o.Method} {OpenApiContract.NormalizePath(o.Path)}")];

    private HashSet<string> MappedApiRoutes()
    {
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        return
        [
            .. from endpoint in dataSource.Endpoints.OfType<RouteEndpoint>()
               let path = OpenApiContract.NormalizePath(endpoint.RoutePattern.RawText ?? "")
               where path == "/api" || path.StartsWith("/api/", StringComparison.Ordinal)
               let methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]
               from method in methods
               select $"{method.ToUpperInvariant()} {path}",
        ];
    }
}
