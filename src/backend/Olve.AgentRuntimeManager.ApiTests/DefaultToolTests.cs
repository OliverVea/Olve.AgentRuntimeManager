using System.Net.Http.Headers;
using System.Net.Http.Json;
using TUnit.Assertions.Enums;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Default tools as a server's configuration gives them (M10): listed as <c>default</c>, given to a
/// session that leaves <c>tools</c> out (and shown on it), never to one that names its own or none.
/// In process only (it needs its own configuration).
/// </summary>
[ClassDataSource<DefaultToolsFactory>(Shared = SharedType.PerClass)]
public class DefaultToolTests(DefaultToolsFactory factory)
{
    private HttpClient Client()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only.");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Tokens.Mint());
        return client;
    }

    [Test]
    public async Task List_MarksTheConfiguredDefaults()
    {
        var tools = (await Client().GetFromJsonAsync<ToolBody[]>("/api/tools", Wire.JsonOptions))!;

        await Assert.That(tools.Where(t => t.Default).Select(t => t.Name)).IsEquivalentTo(["arm_bash", "arm_file_read"]);
        await Assert.That(tools.Count(t => !t.Default)).IsGreaterThan(0);
    }

    [Test]
    public async Task Session_WithoutTools_GetsTheDefaults_InTheirConfiguredOrder()
    {
        var session = await Client().CreateSessionAsync("fake:sleep=0ms");

        await Assert.That(session.Tools).IsEquivalentTo(["arm_file_read", "arm_bash"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Session_NamingTools_GetsExactlyThose()
    {
        var session = await Client().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Tools = ["arm_file_edit"] });

        await Assert.That(session.Tools).IsEquivalentTo(["arm_file_edit"]);
    }

    [Test]
    public async Task Session_WithAnEmptyList_GetsNoTools()
    {
        var session = await Client().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Tools = [] });

        await Assert.That(session.Tools).IsEquivalentTo(Array.Empty<string>());
    }
}
