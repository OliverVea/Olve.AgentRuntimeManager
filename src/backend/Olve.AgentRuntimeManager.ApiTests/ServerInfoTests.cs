using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>Which build a server runs and where: absent on a local run (see <see cref="DeployedServerInfoTests"/>).</summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class ServerInfoTests(ApiTarget target)
{
    [Test]
    public async Task ServerInfo_Local_HasNeither()
    {
        _ = target.Factory; // in-process only: a deployed target has both

        var info = await target.CreateAuthenticatedClient().GetFromJsonAsync<ServerInfoBody>("/api/server-info");

        await Assert.That(info).IsEqualTo(new ServerInfoBody(null, null));
    }
}
