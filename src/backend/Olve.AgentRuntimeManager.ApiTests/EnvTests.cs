using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Registered environment variables (<c>/api/env</c>) and a session's own <c>env</c>. The target is
/// shared (a deployed one too), so each test registers names of its own and deletes them.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class EnvTests(ApiTarget target)
{
    private static string UniqueName() => $"API_TEST_{Guid.NewGuid():N}";

    [Test]
    public async Task Set_ThenList_ShowsIt_ThenDelete_RemovesIt()
    {
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();

        using var set = await client.PutAsync($"/api/env/{name}", Wire.JsonContent(new SetEnvVariableBody("one", Default: false)));
        var stored = (await set.Content.ReadFromJsonAsync<EnvVariableBody>(Wire.JsonOptions))!;
        var listed = (await client.GetFromJsonAsync<EnvVariableBody[]>("/api/env", Wire.JsonOptions))!.SingleOrDefault(v => v.Name == name);
        using var deleted = await client.DeleteAsync($"/api/env/{name}");
        using var again = await client.DeleteAsync($"/api/env/{name}");

        await Assert.That(set.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stored with { UpdatedAt = default }).IsEqualTo(new EnvVariableBody(name, "one", false, default));
        await Assert.That(listed).IsEqualTo(stored);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await again.ErrorAsync()).Code).IsEqualTo("ENV_NOT_FOUND");
    }

    [Test]
    public async Task Set_Twice_ReplacesIt()
    {
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();

        (await client.PutAsync($"/api/env/{name}", Wire.JsonContent(new SetEnvVariableBody("one", Default: false)))).Dispose();
        using var second = await client.PutAsync($"/api/env/{name}", Wire.JsonContent(new SetEnvVariableBody("two", Default: false)));
        var listed = (await client.GetFromJsonAsync<EnvVariableBody[]>("/api/env", Wire.JsonOptions))!.Where(v => v.Name == name).ToList();
        (await client.DeleteAsync($"/api/env/{name}")).Dispose();

        await Assert.That(listed.Select(v => v.Value)).IsEquivalentTo(["two"]);
    }

    [Test]
    [Arguments("CLAUDE_CODE_OAUTH_TOKEN")]
    [Arguments("PATH")]
    [Arguments("ARM_ANYTHING")]
    public async Task Set_AReservedName_Is400(string name)
    {
        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/env/{name}", Wire.JsonContent(new SetEnvVariableBody("x", Default: false)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("RESERVED_ENV_NAME");
    }

    [Test]
    public async Task Set_AnInvalidName_Is400()
    {
        using var response = await target.CreateAuthenticatedClient().PutAsync("/api/env/1BAD-NAME", Wire.JsonContent(new SetEnvVariableBody("x", Default: false)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    [Test]
    public async Task Env_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync("/api/env");

        await Assert.That((int)response.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task Session_EchoesItsOwnEnv_AndTheRegisteredNamesItUses()
    {
        var client = target.CreateAuthenticatedClient();
        var used = UniqueName();
        (await client.PutAsync($"/api/env/{used}", Wire.JsonContent(new SetEnvVariableBody("registered", Default: false)))).Dispose();

        var session = await client.CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms")
        {
            Env = new Dictionary<string, string> { ["MY_VAR"] = "mine" },
            UseEnv = [used],
        });
        (await client.DeleteAsync($"/api/env/{used}")).Dispose();

        await Assert.That(session.Env).IsEquivalentTo(new Dictionary<string, string> { ["MY_VAR"] = "mine" });
        await Assert.That(session.UseEnv).IsEquivalentTo([used]);
    }

    [Test]
    public async Task Session_UsingAnUnknownVariable_Is400()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions",
            Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms") { UseEnv = [UniqueName()] }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("ENV_NOT_FOUND");
    }

    [Test]
    public async Task Session_WithAReservedEnvName_Is400()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions",
            Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms") { Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "/tmp" } }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("RESERVED_ENV_NAME");
    }
}
