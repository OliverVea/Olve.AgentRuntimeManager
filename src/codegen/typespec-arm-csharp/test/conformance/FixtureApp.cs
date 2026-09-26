using Microsoft.AspNetCore.TestHost;
using TUnit.Core.Interfaces;

namespace Arm.Conformance;

/// <summary>
/// The widgets fixture's generated API, wired exactly as a service wires its own
/// (<c>AddArmApi</c> + handler registrations + <c>UseArmApi</c>) and hosted on a
/// <see cref="TestServer"/>. Shared by the tests; the handlers are stateless.
/// </summary>
public sealed class FixtureApp : IAsyncInitializer, IAsyncDisposable
{
    private WebApplication? _app;

    public WebApplication App => _app ?? throw new InvalidOperationException("Not initialized.");

    public IServiceProvider Services => App.Services;

    public HttpClient CreateClient() => App.GetTestClient();

    public async Task InitializeAsync()
    {
        _app = Build(FixtureHandlers.Register);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>Builds (but doesn't start) the fixture app with the given handler registrations.</summary>
    public static WebApplication Build(Action<IServiceCollection> registerHandlers)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddArmApi();
        registerHandlers(builder.Services);

        var app = builder.Build();
        app.UseArmApi();
        return app;
    }
}
