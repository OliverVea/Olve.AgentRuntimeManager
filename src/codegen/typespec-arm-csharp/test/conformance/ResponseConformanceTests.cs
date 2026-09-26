using System.Text;

namespace Arm.Conformance;

/// <summary>
/// One exercised response: an operation, the scenario, the status it must produce, and how to
/// produce it over raw HTTP.
/// </summary>
public sealed record ConformanceCase(
    string OperationId,
    string Scenario,
    int ExpectedStatus,
    Func<HttpClient, Task<HttpResponseMessage>> Send)
{
    public override string ToString() => $"{OperationId} {ExpectedStatus} ({Scenario})";
}

/// <summary>
/// Drives every declared response of every fixture operation (each response variant a handler can
/// answer with, validator and binding failures) and checks the status is declared and the body
/// validates against the contract's schema for it.
/// </summary>
[ClassDataSource<FixtureApp>(Shared = SharedType.PerAssembly)]
public class ResponseConformanceTests(FixtureApp fixture)
{
    public static IEnumerable<Func<ConformanceCase>> Cases()
    {
        // GET /api/widgets (declares no error)
        yield return () => new("Widgets_list", "no parameters", 200, c => c.GetAsync("/api/widgets"));
        yield return () => new("Widgets_list", "query + header parameters", 200, c =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/widgets?color=red&limit=5");
            request.Headers.Add("x-request-id", "abc");
            return c.SendAsync(request);
        });

        // POST /api/widgets (201 | 202 | 400)
        yield return () => new("Widgets_create", "valid body", 201, c => c.PostAsync("/api/widgets", Json(WidgetBody())));
        yield return () => new("Widgets_create", "accepted for later (a second success status)", 202, c => c.PostAsync("/api/widgets", Json(WidgetBody(serial: FixtureHandlers.Queued))));
        yield return () => new("Widgets_create", "all optional properties", 201, c => c.PostAsync("/api/widgets", Json(
            WidgetBody(extra: """ "color":"green","parent":null,"tags":["a"],"labels":{"k":"v"},"weight":7, """))));
        yield return () => new("Widgets_create", "discriminator not first", 201, c => c.PostAsync("/api/widgets", Json(
            WidgetBody(shape: """{"side":3,"kind":"square"}"""))));
        yield return () => new("Widgets_create", "validator failure (name too long)", 400, c => c.PostAsync("/api/widgets", Json(
            WidgetBody(name: new string('x', 51)))));
        yield return () => new("Widgets_create", "validator failures (several problems)", 400, c => c.PostAsync("/api/widgets", Json(
            WidgetBody(name: new string('x', 51), extra: """ "weight":101, """))));
        yield return () => new("Widgets_create", "handler answers 400", 400, c => c.PostAsync("/api/widgets", Json(
            WidgetBody(serial: FixtureHandlers.Invalid))));
        yield return () => new("Widgets_create", "malformed JSON (binding failure)", 400, c => c.PostAsync("/api/widgets", Json("{")));
        yield return () => new("Widgets_create", "missing required property (binding failure)", 400, c => c.PostAsync("/api/widgets", Json("""{"name":"w"}""")));

        // PUT /api/widgets/{id}
        yield return () => new("Widgets_update", "valid body", 200, c => c.PutAsync("/api/widgets/w1?dryRun=true", Json(WidgetBody(extra: """ "note":"n", """))));
        yield return () => new("Widgets_update", "handler answers 404", 404, c => c.PutAsync($"/api/widgets/{FixtureHandlers.Missing}", Json(WidgetBody())));
        yield return () => new("Widgets_update", "handler answers 409", 409, c => c.PutAsync($"/api/widgets/{FixtureHandlers.Conflict}", Json(WidgetBody())));
        yield return () => new("Widgets_update", "handler answers 400", 400, c => c.PutAsync($"/api/widgets/{FixtureHandlers.Invalid}", Json(WidgetBody())));
        yield return () => new("Widgets_update", "validator failure (weight over 100)", 400, c => c.PutAsync("/api/widgets/w1", Json(WidgetBody(extra: """ "weight":101, """))));
        yield return () => new("Widgets_update", "bad query value (binding failure)", 400, c => c.PutAsync("/api/widgets/w1?dryRun=maybe", Json(WidgetBody())));

        // DELETE /api/widgets/{id} (204 | 404)
        yield return () => new("Widgets_delete", "success", 204, c => c.DeleteAsync("/api/widgets/w1"));
        yield return () => new("Widgets_delete", "handler answers 404", 404, c => c.DeleteAsync($"/api/widgets/{FixtureHandlers.Missing}"));

        // GET /api/shapes/{id} (a discriminated union response)
        yield return () => new("Shapes_get", "circle", 200, c => c.GetAsync("/api/shapes/circle"));
        yield return () => new("Shapes_get", "square", 200, c => c.GetAsync("/api/shapes/square"));
        yield return () => new("Shapes_get", "handler answers 404", 404, c => c.GetAsync($"/api/shapes/{FixtureHandlers.Missing}"));

        // GET /api/tags (a required explode:false list)
        yield return () => new("Tags_echo", "a comma list", 200, c => c.GetAsync("/api/tags?names=a,b"));

        // GET /api/widget-events (an SSE stream: every event's data against its event's schema)
        yield return () => new("WidgetEvents_stream", "every event", 200, c => c.GetAsync("/api/widget-events"));
        yield return () => new("WidgetEvents_stream", "filtered by an explode:false list", 200, c => c.GetAsync("/api/widget-events?type=widget.changed,ping"));
        yield return () => new("WidgetEvents_stream", "resumed after Last-Event-ID", 200, c =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/widget-events");
            request.Headers.Add("Last-Event-ID", "1");
            return c.SendAsync(request);
        });
        yield return () => new("WidgetEvents_stream", "handler failure before streaming", 400, c => c.GetAsync("/api/widget-events?type=widget.exploded"));
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Response_MatchesContract(ConformanceCase conformanceCase)
    {
        var operation = OpenApiContract.Operation(conformanceCase.OperationId);

        using var response = await conformanceCase.Send(fixture.CreateClient());
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(status).IsEqualTo(conformanceCase.ExpectedStatus).Because(body);
        await Assert.That(operation.DeclaredStatuses).Contains(status);
        await Assert.That(OpenApiContract.Validate(operation, status, body)).IsEmpty();
    }

    /// <summary>Every status the fixture contract declares is exercised by at least one case above.</summary>
    [Test]
    public async Task EveryDeclaredResponse_IsExercised()
    {
        var exercised = Cases().Select(c => c()).Select(c => $"{c.OperationId} {c.ExpectedStatus}").ToHashSet();
        var declared = OpenApiContract.Operations
            .SelectMany(o => o.DeclaredStatuses.Select(s => $"{o.OperationId} {s}"))
            .ToHashSet();

        await Assert.That(declared).IsNotEmpty();
        await Assert.That(declared.Except(exercised).Order().ToList()).IsEmpty();
    }

    /// <summary>A request body for Widgets_create/update; <paramref name="extra"/> is spliced in verbatim.</summary>
    public static string WidgetBody(
        string serial = "SN-1",
        string name = "w",
        string shape = """{"kind":"circle","radius":1}""",
        string extra = "") =>
        $$"""{"serial":"{{serial}}","name":"{{name}}","priority":2,"description":"d",{{extra}}"createdAt":"2026-01-01T00:00:00Z","shape":{{shape}}}""";

    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
