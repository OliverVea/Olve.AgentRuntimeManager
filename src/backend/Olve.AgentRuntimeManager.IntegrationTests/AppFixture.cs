using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TUnit.Core.Interfaces;

namespace Olve.AgentRuntimeManager.IntegrationTests;

public class AppFixture : IAsyncInitializer, IAsyncDisposable
{
    private const string SigningKey = "integration-test-signing-key-that-is-long-enough";
    private const string Issuer = "integration-test";
    private const string Audience = "integration-test";
    private const int ContainerPort = 5000;

    private IContainer _container = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        // The Dockerfile lives at the repo root (its build context), not next to the solution in src/backend.
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            .Build();

        await image.CreateAsync();

        _container = new ContainerBuilder(image)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithEnvironment("Auth__SigningKey", SigningKey)
            .WithEnvironment("Auth__Authority", Issuer)
            .WithEnvironment("Auth__Audience", Audience)
            .WithEnvironment("Host", "0.0.0.0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/health")))
            .Build();

        await _container.StartAsync();

        var hostPort = _container.GetMappedPublicPort(ContainerPort);
        _baseUrl = $"http://localhost:{hostPort}";
    }

    public HttpClient CreateAuthenticatedHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GenerateJwt());
        return client;
    }

    public HttpClient CreateUnauthenticatedHttpClient() =>
        new() { BaseAddress = new Uri(_baseUrl) };

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private static string GenerateJwt()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = credentials,
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")]),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
