using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>ARM's tools (M10): <c>GET /api/tools</c>, and the tools a session names.</summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class ToolTests(ApiTarget target)
{
    [Test]
    public async Task List_HasArmsTools_EachWithWhatItDoes()
    {
        var tools = (await target.CreateAuthenticatedClient().GetFromJsonAsync<ToolBody[]>("/api/tools", Wire.JsonOptions))!;

        await Assert.That(tools.Select(t => t.Name)).Contains("arm_bash");
        await Assert.That(tools.Select(t => t.Name)).Contains("arm_file_read");
        await Assert.That(tools.Select(t => t.Name).Distinct().Count()).IsEqualTo(tools.Length);
        await Assert.That(tools.All(t => t.Description.Length > 0)).IsTrue();
    }

    [Test]
    public async Task List_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync("/api/tools");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Session_WithKnownTools_HasThemAsGiven()
    {
        var session = await target.CreateAuthenticatedClient().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Tools = ["arm_file_read", "arm_bash"] });

        await Assert.That(session.Tools).IsEquivalentTo(["arm_file_read", "arm_bash"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Session_WithoutTools_GetsExactlyTheServersDefaults()
    {
        var client = target.CreateAuthenticatedClient();
        var defaults = (await client.GetFromJsonAsync<ToolBody[]>("/api/tools", Wire.JsonOptions))!.Where(t => t.Default).Select(t => t.Name).ToList();

        var session = await client.CreateSessionAsync("fake:sleep=0ms");

        // Always stated (never the provider's own tools): the defaults, in the server's order.
        await Assert.That(session.Tools).IsNotNull();
        await Assert.That(session.Tools!).IsEquivalentTo(defaults);
    }

    [Test]
    public async Task ByDefault_EveryToolIsADefault()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only: a deployment may configure its own.");

        var tools = (await target.CreateAuthenticatedClient().GetFromJsonAsync<ToolBody[]>("/api/tools", Wire.JsonOptions))!;

        await Assert.That(tools.All(t => t.Default)).IsTrue();
    }

    [Test]
    public async Task Session_WithAnEmptyList_HasNoTools_EvenWithDefaults()
    {
        var session = await target.CreateAuthenticatedClient().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Tools = [] });

        await Assert.That(session.Tools).IsEquivalentTo(Array.Empty<string>());
    }

    [Test]
    public async Task Session_WithAnUnknownTool_Is400_NamingItAndArmsTools()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions",
            Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms") { Tools = ["arm_bash", "Bash", "nope", "nope"] }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("UNKNOWN_TOOL");
        await Assert.That(error.Message).StartsWith("No tool 'Bash', 'nope'. ARM's tools: ");
        await Assert.That(error.Message).Contains("arm_bash");
    }
}
