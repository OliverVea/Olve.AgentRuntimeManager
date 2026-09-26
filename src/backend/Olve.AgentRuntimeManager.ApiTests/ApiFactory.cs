using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Hosts the API in-process: <see cref="ApiTarget"/>'s default target. Auth uses a symmetric
/// test signing key plus issuer/audience (the same settings a base-URL target is started with),
/// so tests mint their own JWTs (<see cref="TestTokens"/>).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly TestTokens Tokens = new(
        SigningKey: "api-test-signing-key-that-is-long-enough-for-hs256",
        Issuer: "api-test",
        Audience: "api-test");

    // Program.ConfigureHost clears the default configuration sources and reads appsettings*.json
    // from the content root. Point the content root at an empty directory so the deployed
    // appsettings (OTLP exporter, real OIDC authority) don't leak into the tests; these settings
    // arrive as command-line args, which Program adds last and so take precedence.
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("arm-api-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(_contentRoot);
        builder.UseSetting("Auth:SigningKey", Tokens.SigningKey);
        builder.UseSetting("Auth:Authority", Tokens.Issuer);
        builder.UseSetting("Auth:Audience", Tokens.Audience);
        // A database of its own, next to the (empty) content root; a subclass may point elsewhere.
        builder.UseSetting("ConnectionStrings:Arm", ConnectionString(_contentRoot));
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>The SQLite database in <paramref name="folder"/>.</summary>
    public static string ConnectionString(string folder) => $"Data Source={Path.Combine(folder, "arm.db")};Pooling=False";

    /// <summary>
    /// Extra configuration. By default enough slots that concurrently running tests never queue
    /// (a specialised host, e.g. <see cref="SmallQueueFactory"/>, sets its own).
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Sessions:TotalSlots"] = "1000",
    };

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
}
