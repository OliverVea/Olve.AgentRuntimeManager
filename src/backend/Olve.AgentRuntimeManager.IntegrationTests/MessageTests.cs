using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Olve.AgentRuntimeManager.IntegrationTests;

/// <summary>
/// Exercises the API over raw HTTP. Deliberately no generated client: these tests check the wire
/// contract, and a typed client would mask wire-format mistakes.
/// </summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
public class MessageTests(AppFixture fixture)
{
    private sealed record MessageRequest(string Text);
    private sealed record MessageResponse(Guid Id, string Text);
    private sealed record PageResponse(List<MessageResponse> Items, int Page, int PageSize, int TotalCount);

    [Test]
    public async Task CreateMessage_Authenticated_ReturnsCreatedMessage()
    {
        var client = fixture.CreateAuthenticatedHttpClient();

        var created = await CreateAsync(client, "hello");

        await Assert.That(created.Text).IsEqualTo("hello");
        await Assert.That(created.Id).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task CreateMessage_Unauthenticated_Returns401()
    {
        var client = fixture.CreateUnauthenticatedHttpClient();

        var response = await client.PostAsync("/api/messages", new StringContent("{}", Encoding.UTF8, "application/json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CreateMessage_EmptyText_Returns400()
    {
        var client = fixture.CreateAuthenticatedHttpClient();

        var response = await client.PostAsJsonAsync("/api/messages", new MessageRequest(""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetMessages_AfterCreate_IncludesCreatedMessage()
    {
        var client = fixture.CreateAuthenticatedHttpClient();
        var created = await CreateAsync(client, "find-me");

        var page = await GetPageAsync(client);

        await Assert.That(page.Items.Any(m => m.Id == created.Id && m.Text == "find-me")).IsTrue();
        await Assert.That(page.TotalCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task UpdateMessage_ExistingMessage_ChangesText()
    {
        var client = fixture.CreateAuthenticatedHttpClient();
        var created = await CreateAsync(client, "before");

        var response = await client.PutAsJsonAsync($"/api/messages/{created.Id}", new MessageRequest("after"));
        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<MessageResponse>();

        await Assert.That(updated!.Id).IsEqualTo(created.Id);
        await Assert.That(updated.Text).IsEqualTo("after");
    }

    [Test]
    public async Task DeleteMessage_ExistingMessage_RemovesIt()
    {
        var client = fixture.CreateAuthenticatedHttpClient();
        var created = await CreateAsync(client, "delete-me");

        var response = await client.DeleteAsync($"/api/messages/{created.Id}");
        response.EnsureSuccessStatusCode();

        var page = await GetPageAsync(client);
        await Assert.That(page.Items.Any(m => m.Id == created.Id)).IsFalse();
    }

    private static async Task<MessageResponse> CreateAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/messages", new MessageRequest(text));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MessageResponse>())!;
    }

    private static async Task<PageResponse> GetPageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PageResponse>("/api/messages"))!;
}
