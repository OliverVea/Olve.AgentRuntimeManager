using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>Which build a server runs and where, on a host configured the way a deploy configures it.</summary>
[ClassDataSource<DeployedFactory>(Shared = SharedType.PerClass)]
public class DeployedServerInfoTests(DeployedFactory factory)
{
    [Test]
    public async Task ServerInfo_Deployed_ServesTheVersionAndEnvironment()
    {
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 }, "In-process only.");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Tokens.Mint());

        var info = await client.GetFromJsonAsync<ServerInfoBody>("/api/server-info");

        await Assert.That(info).IsEqualTo(new ServerInfoBody(DeployedFactory.Version, DeployedFactory.Environment));
    }
}
