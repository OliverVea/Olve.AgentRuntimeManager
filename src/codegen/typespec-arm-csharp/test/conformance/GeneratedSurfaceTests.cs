using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;
using System.Text.Json;
using Olve.Results;

namespace Arm.Conformance;

/// <summary>
/// The rest of the generated surface: the operation table matches the contract, parameters bind
/// from where the contract puts them, <see cref="ArmResults"/>' status rules (including the
/// undeclared cases the response cases can't validate), binding failures on operations without a
/// declared 400, and the startup check for missing handlers.
/// </summary>
[ClassDataSource<FixtureApp>(Shared = SharedType.PerAssembly)]
public class GeneratedSurfaceTests(FixtureApp fixture)
{
    private static IReadOnlyList<ArmOperation> GeneratedOperations =>
        [.. typeof(ArmOperations).GetFields().Select(f => (ArmOperation)f.GetValue(null)!)];

    [Test]
    public async Task OperationTable_MatchesContractStatuses()
    {
        var generated = GeneratedOperations
            .Select(o => $"{o.OperationId}: {string.Join(",", o.ErrorStatuses.Prepend(o.SuccessStatus).Order())}")
            .Order()
            .ToList();
        var spec = OpenApiContract.Operations
            .Select(o => $"{o.OperationId}: {string.Join(",", o.DeclaredStatuses.Order())}")
            .Order()
            .ToList();

        await Assert.That(generated).IsNotEmpty();
        await Assert.That(generated).IsEquivalentTo(spec);
    }

    [Test]
    public async Task EveryHandlerType_IsRegisteredByTheFixture()
    {
        await Assert.That(ArmApi.HandlerTypes.Count).IsEqualTo(GeneratedOperations.Count);
        await Assert.That(fixture.Services.MissingHandlers()).IsEmpty();
    }

    [Test]
    public async Task UseArmApi_WithAMissingHandler_Throws()
    {
        var build = () => FixtureApp.Build(services =>
        {
            FixtureHandlers.Register(services);
            services.RemoveAll<IShapesGetHandler>();
        });

        await Assert.That(build).Throws<InvalidOperationException>().WithMessageContaining(nameof(IShapesGetHandler));
    }

    [Test]
    public async Task QueryAndHeaderParameters_Bind()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/widgets?color=green&limit=7");
        request.Headers.Add("x-request-id", "req-1");

        using var response = await fixture.CreateClient().SendAsync(request);
        var widgets = await response.Content.ReadFromJsonAsync<JsonElement>();

        var labels = widgets[0].GetProperty("labels");
        await Assert.That(labels.GetProperty("color").GetString()).IsEqualTo(nameof(Color.Green));
        await Assert.That(labels.GetProperty("limit").GetString()).IsEqualTo("7");
        await Assert.That(labels.GetProperty("requestId").GetString()).IsEqualTo("req-1");
    }

    [Test]
    public async Task PathQueryAndBodyParameters_Bind()
    {
        var body = ResponseConformanceTests.WidgetBody(serial: "SN-9", name: "renamed", extra: """ "note":"hi", """);

        using var response = await fixture.CreateClient().PutAsync("/api/widgets/w42?dryRun=false", ResponseConformanceTests.Json(body));
        var widget = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        var labels = widget.GetProperty("labels");
        await Assert.That(labels.GetProperty("id").GetString()).IsEqualTo("w42");
        await Assert.That(labels.GetProperty("dryRun").GetString()).IsEqualTo("false");
        await Assert.That(labels.GetProperty("note").GetString()).IsEqualTo("hi");
        await Assert.That(widget.GetProperty("serial").GetString()).IsEqualTo("SN-9");
        await Assert.That(widget.GetProperty("name").GetString()).IsEqualTo("renamed");
    }

    [Test]
    public async Task RequestBody_RoundTripsUnionsNullablesAndOptionals()
    {
        var body = ResponseConformanceTests.WidgetBody(
            shape: """{"side":3,"kind":"square"}""",
            extra: """ "color":"red","parent":null,"tags":["a","b"],"labels":{"k":"v"},"weight":0, """);

        using var response = await fixture.CreateClient().PostAsync("/api/widgets", ResponseConformanceTests.Json(body));
        var widget = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(widget.GetProperty("shape").GetProperty("kind").GetString()).IsEqualTo("square");
        await Assert.That(widget.GetProperty("shape").GetProperty("side").GetDouble()).IsEqualTo(3);
        await Assert.That(widget.GetProperty("color").GetString()).IsEqualTo("red");
        await Assert.That(widget.GetProperty("parent").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(widget.GetProperty("weight").GetInt32()).IsEqualTo(0);
        await Assert.That(widget.GetProperty("tags").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task EventStream_NamesEachEventByItsType_AndPassesIdsThrough()
    {
        using var response = await fixture.CreateClient().GetAsync("/api/widget-events");
        var events = OpenApiContract.ParseEvents(await response.Content.ReadAsStringAsync());

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(events.Select(e => $"{e.EventType}#{e.EventId}").ToList())
            .IsEquivalentTo(["ping#", "widget.changed#1", "widget.removed#2"]);
        foreach (var e in events)
        {
            using var data = JsonDocument.Parse(e.Data);
            await Assert.That(data.RootElement.GetProperty("type").GetString()).IsEqualTo(e.EventType);
        }
    }

    [Test]
    public async Task EventStream_BindsExplodeFalseListsAndHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/widget-events?type=widget.removed, widget.changed,");
        request.Headers.Add("Last-Event-ID", "1");

        using var response = await fixture.CreateClient().SendAsync(request);
        var events = OpenApiContract.ParseEvents(await response.Content.ReadAsStringAsync());

        await Assert.That(events.Select(e => e.EventType).ToList()).IsEquivalentTo(["widget.removed"]);
    }

    [Test]
    public async Task RequiredExplodeFalseList_SplitsTrimsAndDropsEmptyEntries()
    {
        var names = await fixture.CreateClient().GetFromJsonAsync<string[]>("/api/tags?names=a, b,,c");

        await Assert.That(names).IsEquivalentTo(["a", "b", "c"]);
    }

    [Test]
    public async Task HandlerFailure_OnOperationDeclaringNoErrors_Is500WithProblems()
    {
        using var response = await fixture.CreateClient().GetAsync("/api/widgets?limit=-1");
        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That((int)response.StatusCode).IsEqualTo(500);
        await Assert.That(problems[0].GetProperty("message").GetString()).IsEqualTo("'limit' cannot be negative.");
    }

    [Test]
    [Arguments("/api/widgets?limit=abc")]
    [Arguments("/api/widgets?color=blue")]
    [Arguments("/api/tags")]
    public async Task BindingFailure_OnOperationWithoutDeclared400_IsBodyless400(string url)
    {
        using var response = await fixture.CreateClient().GetAsync(url);

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEmpty();
    }

    [Test]
    public async Task BindingFailure_OnOperationWithDeclared400_ExplainsTheFailure()
    {
        using var response = await fixture.CreateClient().PostAsync("/api/widgets", ResponseConformanceTests.Json("{"));
        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
        await Assert.That(problems.GetArrayLength()).IsEqualTo(1);
        await Assert.That(problems[0].GetProperty("message").GetString()).IsNotNull().And.IsNotEmpty();
    }

    [Test]
    [Arguments(new[] { "http:404" }, new[] { 400, 404 }, 404)]
    [Arguments(new[] { "http:409" }, new[] { 400, 404 }, 400)]
    [Arguments(new[] { "http:409" }, new[] { 404 }, 404)]
    [Arguments(new[] { "other", "http:404" }, new[] { 400, 404 }, 404)]
    [Arguments(new[] { "http:x" }, new[] { 404, 400 }, 400)]
    [Arguments(new string[0], new int[0], 500)]
    public async Task StatusFor_FollowsTheDeclaredStatusRules(string[] tags, int[] declared, int expected)
    {
        var operation = new ArmOperation("Op", 200, declared);
        var problems = new[] { new ResultProblem("p") { Tags = tags } };

        await Assert.That(ArmResults.StatusFor(problems, operation)).IsEqualTo(expected);
    }
}
