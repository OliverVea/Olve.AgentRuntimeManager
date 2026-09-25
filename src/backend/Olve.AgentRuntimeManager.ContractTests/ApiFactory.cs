using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TUnit.Core.Interfaces;

namespace Olve.AgentRuntimeManager.ContractTests;

/// <summary>
/// Hosts the API in-process. Auth is configured the same way the integration tests' AppFixture
/// does it (a symmetric test signing key plus issuer/audience), so tests can mint their own JWTs.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncInitializer
{
    public const string SigningKey = "contract-test-signing-key-that-is-long-enough";
    private const string Issuer = "contract-test";
    private const string Audience = "contract-test";

    // Program.ConfigureHost clears the default configuration sources and reads appsettings*.json
    // from the content root. Point the content root at an empty directory so the deployed
    // appsettings (OTLP exporter, real OIDC authority) don't leak into the tests; these settings
    // arrive as command-line args, which Program adds last and so take precedence.
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("arm-contract-").FullName;

    public Task InitializeAsync()
    {
        // Force the host to start so failures surface in fixture setup, not in the first test.
        _ = Server;
        return Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(_contentRoot);
        builder.UseSetting("Auth:SigningKey", SigningKey);
        builder.UseSetting("Auth:Authority", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt());
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover empty temp directory is harmless.
        }
    }

    private static string GenerateJwt()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, "contract-test-user")]),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
