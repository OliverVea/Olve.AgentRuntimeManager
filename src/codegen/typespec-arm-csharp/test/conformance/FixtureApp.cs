using Microsoft.AspNetCore.TestHost;
using Olve.Results;
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

/// <summary>
/// Trivial handlers for the fixture's operations. They echo what they received (so tests can
/// check parameter binding without shared state) and fail on magic ids, so tests can drive every
/// <see cref="ArmResults"/> status rule:
/// <list type="bullet">
///   <item><c>missing</c>: a problem tagged <c>http:404</c>.</item>
///   <item><c>conflict</c>: a problem tagged <c>http:409</c>, which no operation declares.</item>
///   <item><c>untagged</c>: a problem without a status tag.</item>
/// </list>
/// </summary>
public static class FixtureHandlers
{
    public const string Missing = "missing";
    public const string Conflict = "conflict";
    public const string Untagged = "untagged";

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IWidgetsListHandler, ListWidgets>();
        services.AddSingleton<IWidgetsCreateHandler, CreateWidget>();
        services.AddSingleton<IWidgetsUpdateHandler, UpdateWidget>();
        services.AddSingleton<IWidgetsDeleteHandler, DeleteWidget>();
        services.AddSingleton<IShapesGetHandler, GetShape>();
    }

    /// <summary>The failure a magic id asks for, if any.</summary>
    public static ResultProblem? FailureFor(string id) => id switch
    {
        Missing => new ResultProblem("'{0}' was not found.", id) { Tags = [ArmResults.StatusTag(404)] },
        Conflict => new ResultProblem("'{0}' conflicts.", id) { Tags = [ArmResults.StatusTag(409)] },
        Untagged => new ResultProblem("'{0}' failed.", id),
        _ => null,
    };

    public static Widget SampleWidget(IReadOnlyDictionary<string, string>? labels = null) => new()
    {
        Id = Guid.NewGuid(),
        Serial = "SN-1",
        Name = "sample",
        Priority = Priority.High,
        Description = null,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Shape = new Circle { Radius = 1 },
        Labels = labels,
    };

    private sealed class ListWidgets : IWidgetsListHandler
    {
        public Task<Result<IReadOnlyList<Widget>>> HandleAsync(WidgetsListRequest request, CancellationToken cancellationToken)
        {
            // A negative limit fails; Widgets_list declares no error status, so ArmResults answers 500.
            if (request.Limit < 0)
            {
                return Task.FromResult<Result<IReadOnlyList<Widget>>>(new ResultProblem("'limit' cannot be negative."));
            }

            var labels = new Dictionary<string, string>();
            if (request.Color is { } color)
            {
                labels["color"] = color.ToString();
            }

            if (request.Limit is { } limit)
            {
                labels["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (request.RequestId is { } requestId)
            {
                labels["requestId"] = requestId;
            }

            return Task.FromResult<Result<IReadOnlyList<Widget>>>(new[] { SampleWidget(labels) });
        }
    }

    private sealed class CreateWidget : IWidgetsCreateHandler
    {
        public Task<Result<Widget>> HandleAsync(WidgetsCreateRequest request, CancellationToken cancellationToken)
        {
            var body = request.Body;
            if (FailureFor(body.Serial) is { } problem)
            {
                return Task.FromResult<Result<Widget>>(problem);
            }

            return Task.FromResult<Result<Widget>>(new Widget
            {
                Id = Guid.NewGuid(),
                Serial = body.Serial,
                Name = body.Name,
                Color = body.Color,
                Priority = body.Priority,
                Description = body.Description,
                Parent = body.Parent,
                Tags = body.Tags,
                Labels = body.Labels,
                Weight = body.Weight,
                CreatedAt = body.CreatedAt,
                Shape = body.Shape,
            });
        }
    }

    private sealed class UpdateWidget : IWidgetsUpdateHandler
    {
        public Task<Result<Widget>> HandleAsync(WidgetsUpdateRequest request, CancellationToken cancellationToken)
        {
            if (FailureFor(request.Id) is { } problem)
            {
                return Task.FromResult<Result<Widget>>(problem);
            }

            var body = request.Body;
            var labels = new Dictionary<string, string> { ["id"] = request.Id };
            if (request.DryRun is { } dryRun)
            {
                labels["dryRun"] = dryRun ? "true" : "false";
            }

            if (body.Note is { } note)
            {
                labels["note"] = note;
            }

            return Task.FromResult<Result<Widget>>(SampleWidget(labels) with
            {
                Serial = body.Serial,
                Name = body.Name,
                Description = body.Description,
                Shape = body.Shape,
            });
        }
    }

    private sealed class DeleteWidget : IWidgetsDeleteHandler
    {
        public Task<Result> RunAsync(WidgetsDeleteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(FailureFor(request.Id) is { } problem ? (Result)problem : Result.Success());
    }

    private sealed class GetShape : IShapesGetHandler
    {
        public Task<Result<Shape>> HandleAsync(ShapesGetRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<Result<Shape>>(request.Id switch
            {
                "circle" => new Circle { Radius = 1.5 },
                "square" => new Square { Side = 2 },
                _ => FailureFor(request.Id) ?? new ResultProblem("Shape '{0}' was not found.", request.Id) { Tags = [ArmResults.StatusTag(404)] },
            });
    }
}
