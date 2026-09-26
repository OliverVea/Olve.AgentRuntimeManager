using System.Net.Http.Headers;
using TUnit.Core.Interfaces;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// The server the API tests talk to (docs/TESTING.md, "One HTTP suite, two targets"):
/// <list type="bullet">
///   <item>default: in-process (<see cref="ApiFactory"/>); what <c>dotnet test</c> and CI run.</item>
///   <item>base-URL mode: when <c>ARM_API_BASE_URL</c> is set, any running server (a local
///   <c>dotnet run</c>, the Docker image via <c>mise run api:image</c>, later live beta). To mint
///   tokens it needs the server's <c>ARM_API_SIGNING_KEY</c>, <c>ARM_API_ISSUER</c> and
///   <c>ARM_API_AUDIENCE</c>; without them, tests that need a token are skipped.</item>
/// </list>
/// Tests that inspect the host itself (DI, configuration) run in-process only.
/// </summary>
public sealed class ApiTarget : IAsyncInitializer, IAsyncDisposable
{
    public const string BaseUrlVariable = "ARM_API_BASE_URL";
    public const string SigningKeyVariable = "ARM_API_SIGNING_KEY";
    public const string IssuerVariable = "ARM_API_ISSUER";
    public const string AudienceVariable = "ARM_API_AUDIENCE";

    private readonly Uri? _baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable) is { Length: > 0 } url ? new Uri(url) : null;
    private ApiFactory? _factory;

    public bool IsInProcess => _baseUrl is null;

    /// <summary>The token settings, or null in base-URL mode when the environment doesn't provide them.</summary>
    public TestTokens? Tokens { get; } = Environment.GetEnvironmentVariable(BaseUrlVariable) is { Length: > 0 }
        ? FromEnvironment()
        : ApiFactory.Tokens;

    /// <summary>The in-process host; skips the calling test on a base-URL target.</summary>
    public ApiFactory Factory
    {
        get
        {
            Skip.Unless(IsInProcess, $"Inspects the in-process host; not available with {BaseUrlVariable} set.");
            return _factory!;
        }
    }

    public async Task InitializeAsync()
    {
        if (IsInProcess)
        {
            _factory = new ApiFactory();
            _ = _factory.Server; // start now, so host failures surface in setup, not in the first test
            return;
        }

        // Fail fast (and clearly) if nothing is listening.
        using var client = CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
    }

    /// <remarks>
    /// One at a time: the factory records its clients in a plain list (to dispose them with it), and
    /// tests creating clients in parallel could corrupt it (then disposing throws a NullReferenceException).
    /// </remarks>
    public HttpClient CreateClient()
    {
        if (!IsInProcess)
        {
            return new HttpClient { BaseAddress = _baseUrl };
        }

        lock (_clients)
        {
            return _factory!.CreateClient();
        }
    }

    private readonly Lock _clients = new();

    /// <summary>A client with a valid bearer token; skips the calling test if none can be minted.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        Skip.When(Tokens is null,
            $"Needs {SigningKeyVariable}, {IssuerVariable} and {AudienceVariable} (the target's Auth settings) to mint a token.");

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens!.Mint());
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private static TestTokens? FromEnvironment() =>
        (Environment.GetEnvironmentVariable(SigningKeyVariable),
         Environment.GetEnvironmentVariable(IssuerVariable),
         Environment.GetEnvironmentVariable(AudienceVariable)) is ({ Length: > 0 } key, { Length: > 0 } issuer, { Length: > 0 } audience)
            ? new TestTokens(key, issuer, audience)
            : null;
}
