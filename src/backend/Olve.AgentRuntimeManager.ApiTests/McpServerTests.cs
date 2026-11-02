using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Registered MCP servers (<c>/api/mcp-servers</c>, M10c), and sessions naming them in <c>tools</c>.
/// The target is shared (a deployed one too), so each test registers a name of its own and deletes it.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class McpServerTests(ApiTarget target)
{
    private static string UniqueName() => $"api-test-{Guid.NewGuid():N}"[..32];

    private static readonly SetMcpServerBody Server = new("Reads things.", "npx", ["-y", "pkg@1.2.3"], new() { ["SOME_TOKEN"] = "t" });

    [Test]
    public async Task Set_ThenGetAndList_ShowIt_ThenDelete_RemovesIt()
    {
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();

        using var set = await client.PutAsync($"/api/mcp-servers/{name}", Wire.JsonContent(Server));
        var stored = (await set.Content.ReadFromJsonAsync<McpServerBody>(Wire.JsonOptions))!;
        var got = await client.GetFromJsonAsync<McpServerBody>($"/api/mcp-servers/{name}", Wire.JsonOptions);
        var listed = (await client.GetFromJsonAsync<McpServerBody[]>("/api/mcp-servers", Wire.JsonOptions))!.Single(s => s.Name == name);
        using var deleted = await client.DeleteAsync($"/api/mcp-servers/{name}");
        using var gone = await client.GetAsync($"/api/mcp-servers/{name}");
        using var again = await client.DeleteAsync($"/api/mcp-servers/{name}");

        await Assert.That(set.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((stored.Name, stored.Description, stored.Command)).IsEqualTo((name, "Reads things.", "npx"));
        await Assert.That(stored.Args).IsEquivalentTo(["-y", "pkg@1.2.3"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(stored.Env["SOME_TOKEN"]).IsEqualTo("t");
        await Assert.That(got!.Args).IsEquivalentTo(stored.Args, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(listed.UpdatedAt).IsEqualTo(stored.UpdatedAt);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await gone.ErrorAsync()).Message).IsEqualTo($"No MCP server '{name}' is registered.");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await again.ErrorAsync()).Code).IsEqualTo("MCP_SERVER_NOT_FOUND");
    }

    [Test]
    public async Task Set_WithOnlyWhatsRequired_HasNoArgsOrEnv_AndTwiceReplacesIt()
    {
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();

        (await client.PutAsync($"/api/mcp-servers/{name}", Wire.JsonContent(Server))).Dispose();
        using var second = await client.PutAsync($"/api/mcp-servers/{name}", Wire.JsonContent(new SetMcpServerBody("Other.", "uvx")));
        var replaced = (await second.Content.ReadFromJsonAsync<McpServerBody>(Wire.JsonOptions))!;
        (await client.DeleteAsync($"/api/mcp-servers/{name}")).Dispose();

        await Assert.That(replaced.Command).IsEqualTo("uvx");
        await Assert.That(replaced.Args).IsEmpty();
        await Assert.That(replaced.Env).IsEmpty();
    }

    [Test]
    [Arguments("arm", "RESERVED_MCP_SERVER_NAME", "'arm' is reserved for ARM ('arm' and names starting with 'arm-').")]
    [Arguments("arm-tools", "RESERVED_MCP_SERVER_NAME", "'arm-tools' is reserved for ARM ('arm' and names starting with 'arm-').")]
    [Arguments("arm_bash", "INVALID_REQUEST", "'arm_bash' is not a valid MCP server name (lowercase letters, digits and '-', starting with a letter, at most 32 characters).")]
    [Arguments("1server", "INVALID_REQUEST", "'1server' is not a valid MCP server name (lowercase letters, digits and '-', starting with a letter, at most 32 characters).")]
    [Arguments("a-name-of-thirty-three-characters", "INVALID_REQUEST", "'a-name-of-thirty-three-characters' is not a valid MCP server name (lowercase letters, digits and '-', starting with a letter, at most 32 characters).")]
    public async Task Set_ABadName_Is400(string name, string code, string message)
    {
        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/mcp-servers/{name}", Wire.JsonContent(Server));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo(code);
        await Assert.That(error.Message).IsEqualTo(message);
    }

    [Test]
    [Arguments("MCP_TIMEOUT")]
    [Arguments("CLAUDE_CODE_OAUTH_TOKEN")]
    public async Task Set_WithAReservedVariable_Is400(string variable)
    {
        var body = Server with { Env = new() { [variable] = "x" } };

        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/mcp-servers/{UniqueName()}", Wire.JsonContent(body));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("RESERVED_ENV_NAME");
        await Assert.That(error.Message).IsEqualTo($"'{variable}' is set by ARM for its agents and can't be given.");
    }

    [Test]
    [Arguments("", "npx")]
    [Arguments("Reads things.", "")]
    public async Task Set_WithABlankRequiredField_Is400(string description, string command)
    {
        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/mcp-servers/{UniqueName()}", Wire.JsonContent(new SetMcpServerBody(description, command)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    [Test]
    public async Task ARegisteredServer_IsATool_ThatsNeverADefault_AndASessionNamesIt()
    {
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();
        (await client.PutAsync($"/api/mcp-servers/{name}", Wire.JsonContent(Server))).Dispose();

        var tool = (await client.GetFromJsonAsync<ToolBody[]>("/api/tools", Wire.JsonOptions))!.Single(t => t.Name == name);
        var named = await client.CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Tools = ["arm_bash", name] });
        var unnamed = await client.CreateSessionAsync("fake:sleep=0ms");
        (await client.DeleteAsync($"/api/mcp-servers/{name}")).Dispose();

        await Assert.That(tool).IsEqualTo(new ToolBody(name, "Reads things.", false));
        await Assert.That(named.Tools).IsEquivalentTo(["arm_bash", name], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(unnamed.Tools!).DoesNotContain(name);
    }

    [Test]
    public async Task Session_NamingADeletedServer_Is400()
    {
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();
        (await client.PutAsync($"/api/mcp-servers/{name}", Wire.JsonContent(Server))).Dispose();
        (await client.DeleteAsync($"/api/mcp-servers/{name}")).Dispose();

        using var response = await client.PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms") { Tools = [name] }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("UNKNOWN_TOOL");
    }

    [Test]
    public async Task List_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync("/api/mcp-servers");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
