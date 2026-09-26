using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Which operations need a user: writes and the event stream do, reads and the SPA's auth
/// config don't. Also the auth config ARM serves.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class AuthTests(ApiTarget target)
{
    private const string SomeId = "0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11";

    public static IEnumerable<Func<(string Method, string Path)>> ProtectedOperations()
    {
        yield return () => ("POST", "/api/messages");
        yield return () => ("PUT", $"/api/messages/{SomeId}");
        yield return () => ("DELETE", $"/api/messages/{SomeId}");
        yield return () => ("GET", "/api/events");
    }

    [Test]
    [MethodDataSource(nameof(ProtectedOperations))]
    public async Task ProtectedOperation_WithoutToken_Is401(string method, string path)
    {
        using var response = await target.CreateClient().SendAsync(Request(method, path));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [MethodDataSource(nameof(ProtectedOperations))]
    public async Task ProtectedOperation_WithInvalidToken_Is401(string method, string path)
    {
        var client = target.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        using var response = await client.SendAsync(Request(method, path));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("/api/messages", HttpStatusCode.OK)]
    [Arguments($"/api/messages/{SomeId}", HttpStatusCode.NotFound)]
    [Arguments("/api/auth-config", HttpStatusCode.OK)]
    [Arguments("/health", HttpStatusCode.OK)]
    public async Task AnonymousOperation_WithoutToken_IsServed(string path, HttpStatusCode expected)
    {
        using var response = await target.CreateClient().GetAsync(path);

        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }

    [Test]
    public async Task AuthConfig_ServesTheLoginSettings()
    {
        var config = await target.CreateClient().GetFromJsonAsync<AuthConfigBody>("/api/auth-config");

        await Assert.That(config!.Scopes.Split(' ')).Contains("openid");
        if (target.Tokens is { } tokens)
        {
            // No dedicated frontend provider configured on the test targets: the resource authority.
            await Assert.That(config.Authority).IsEqualTo(tokens.Issuer);
        }
    }

    [Test]
    public async Task AuthConfig_InProcess_UsesTheDefaults()
    {
        _ = target.Factory; // in-process only: a deployed target may configure a frontend client

        var config = await target.CreateClient().GetFromJsonAsync<AuthConfigBody>("/api/auth-config");

        await Assert.That(config).IsEqualTo(new AuthConfigBody(ApiFactory.Tokens.Issuer, null, "openid profile email offline_access"));
    }

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path) { Content = method is "POST" or "PUT" ? Wire.TextBody("hello") : null };
}
