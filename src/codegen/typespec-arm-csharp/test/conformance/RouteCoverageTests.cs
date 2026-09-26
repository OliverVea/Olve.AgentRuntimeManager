namespace Arm.Conformance;

/// <summary>
/// <c>MapArmApi</c> routes every operation in the contract, and every <c>/api</c> endpoint it maps
/// is in the contract (docs/STANDARDS.md: "every operation MUST be implemented, and every /api
/// endpoint MUST be in the contract"): checked once here, for the generator, not per service.
/// </summary>
[ClassDataSource<FixtureApp>(Shared = SharedType.PerAssembly)]
public class RouteCoverageTests(FixtureApp fixture)
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
    [Arguments("/api/widgets/{id}", "/api/widgets/{}")]
    [Arguments("/api/widgets/{id:guid}", "/api/widgets/{}")]
    [Arguments("api/widgets/{id?}/", "/api/widgets/{}")]
    [Arguments("/API/Widgets", "/api/widgets")]
    public async Task NormalizePath_IgnoresParameterNamesAndConstraints(string template, string expected) =>
        await Assert.That(OpenApiContract.NormalizePath(template)).IsEqualTo(expected);

    private static HashSet<string> SpecRoutes() =>
        [.. OpenApiContract.Operations.Select(o => $"{o.Method} {OpenApiContract.NormalizePath(o.Path)}")];

    private HashSet<string> MappedApiRoutes()
    {
        var dataSource = fixture.Services.GetRequiredService<EndpointDataSource>();
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
