using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// The <c>/api/messages</c> operations as ARM implements them: statuses, bodies and messages for
/// the happy paths, validation, unknown and malformed ids. Messages persist across tests (and, on a
/// base-URL target, across runs), so tests create what they read and never assume counts.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class MessageTests(ApiTarget target)
{
    private static string MissingId => Guid.NewGuid().ToString();

    // --- create --------------------------------------------------------------------------------

    [Test]
    public async Task Create_ReturnsTheMessageWithANewId()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/messages", Wire.TextBody("hello"));
        var created = await response.Content.ReadFromJsonAsync<MessageBody>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(created!.Text).IsEqualTo("hello");
        await Assert.That(created.Id).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task Create_TwoMessages_GetDistinctIds()
    {
        var client = target.CreateAuthenticatedClient();

        var first = await client.CreateMessageAsync("one");
        var second = await client.CreateMessageAsync("two");

        await Assert.That(first.Id).IsNotEqualTo(second.Id);
    }

    [Test]
    public async Task Create_ThenListAndGet_ReturnIt()
    {
        var client = target.CreateAuthenticatedClient();
        var created = await client.CreateMessageAsync("find-me");

        var listed = await client.ListAllMessagesAsync();
        var fetched = await client.GetFromJsonAsync<MessageBody>($"/api/messages/{created.Id}");

        await Assert.That(listed).Contains(created);
        await Assert.That(fetched).IsEqualTo(created);
    }

    [Test]
    public async Task Create_TextOf280Characters_IsAccepted()
    {
        var text = new string('x', 280);

        var created = await target.CreateAuthenticatedClient().CreateMessageAsync(text);

        await Assert.That(created.Text).IsEqualTo(text);
    }

    [Test]
    public async Task Create_TextOver280Characters_Is400()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/messages", Wire.TextBody(new string('x', 281)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["'text' cannot exceed 280 characters."]);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\t\n")]
    public async Task Create_BlankText_Is400(string text)
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/messages", Wire.TextBody(text));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["'text' cannot be empty."]);
    }

    [Test]
    public async Task Create_NullText_Is400()
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/messages", Wire.Json("""{"text":null}"""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["'text' is required."]);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{")]
    public async Task Create_UnreadableBody_Is400(string body)
    {
        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/messages", Wire.Json(body));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).HasSingleItem();
    }

    // --- get -----------------------------------------------------------------------------------

    [Test]
    public async Task Get_UnknownId_Is404()
    {
        var id = MissingId;

        using var response = await target.CreateClient().GetAsync($"/api/messages/{id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo([$"Message with id '{id}' was not found."]);
    }

    [Test]
    public async Task Get_MalformedId_Is404()
    {
        using var response = await target.CreateClient().GetAsync("/api/messages/not-a-guid");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["Message with id 'not-a-guid' was not found."]);
    }

    // --- update --------------------------------------------------------------------------------

    [Test]
    public async Task Update_ReplacesTheText()
    {
        var client = target.CreateAuthenticatedClient();
        var created = await client.CreateMessageAsync("before");

        using var response = await client.PutAsync($"/api/messages/{created.Id}", Wire.TextBody("after"));
        var updated = await response.Content.ReadFromJsonAsync<MessageBody>();
        var fetched = await client.GetFromJsonAsync<MessageBody>($"/api/messages/{created.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(updated).IsEqualTo(created with { Text = "after" });
        await Assert.That(fetched).IsEqualTo(updated);
    }

    [Test]
    public async Task Update_BlankText_Is400AndKeepsTheMessage()
    {
        var client = target.CreateAuthenticatedClient();
        var created = await client.CreateMessageAsync("keep me");

        using var response = await client.PutAsync($"/api/messages/{created.Id}", Wire.TextBody(" "));
        var fetched = await client.GetFromJsonAsync<MessageBody>($"/api/messages/{created.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["'text' cannot be empty."]);
        await Assert.That(fetched).IsEqualTo(created);
    }

    [Test]
    public async Task Update_TextOver280Characters_Is400()
    {
        var client = target.CreateAuthenticatedClient();
        var created = await client.CreateMessageAsync("short");

        using var response = await client.PutAsync($"/api/messages/{created.Id}", Wire.TextBody(new string('x', 281)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["'text' cannot exceed 280 characters."]);
    }

    /// <summary>
    /// The spec declares no 404 for update yet (M4), so not-found answers 400. The message is the
    /// store's own (<c>EntityStore.Mutate</c>), unlike get/delete's <c>MessageMapping.NotFound</c>.
    /// </summary>
    [Test]
    public async Task Update_UnknownId_Is400NotFound()
    {
        var id = MissingId;

        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/messages/{id}", Wire.TextBody("after"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo([$"Entity with id '{id}' not found."]);
    }

    [Test]
    public async Task Update_MalformedId_Is400NotFound()
    {
        using var response = await target.CreateAuthenticatedClient().PutAsync("/api/messages/not-a-guid", Wire.TextBody("after"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["Message with id 'not-a-guid' was not found."]);
    }

    // --- delete --------------------------------------------------------------------------------

    [Test]
    public async Task Delete_RemovesTheMessage()
    {
        var client = target.CreateAuthenticatedClient();
        var created = await client.CreateMessageAsync("delete-me");

        using var response = await client.DeleteAsync($"/api/messages/{created.Id}");
        using var fetched = await client.GetAsync($"/api/messages/{created.Id}");
        var listed = await client.ListAllMessagesAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEmpty();
        await Assert.That(fetched.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(listed.Select(m => m.Id)).DoesNotContain(created.Id);
    }

    /// <summary>The spec declares no 404 for delete yet (M4), so not-found answers 400.</summary>
    [Test]
    public async Task Delete_Twice_SecondIs400NotFound()
    {
        var client = target.CreateAuthenticatedClient();
        var created = await client.CreateMessageAsync("delete-me-twice");
        (await client.DeleteAsync($"/api/messages/{created.Id}")).EnsureSuccessStatusCode();

        using var response = await client.DeleteAsync($"/api/messages/{created.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo([$"Message with id '{created.Id}' was not found."]);
    }

    [Test]
    public async Task Delete_MalformedId_Is400NotFound()
    {
        using var response = await target.CreateAuthenticatedClient().DeleteAsync("/api/messages/not-a-guid");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.ProblemMessagesAsync()).IsEquivalentTo(["Message with id 'not-a-guid' was not found."]);
    }

    // --- list ----------------------------------------------------------------------------------

    [Test]
    public async Task List_Defaults_ToTheFirstPageOf20()
    {
        var page = await target.CreateClient().GetFromJsonAsync<PageBody>("/api/messages");

        await Assert.That(page!.PageNumber).IsEqualTo(1);
        await Assert.That(page.PageSize).IsEqualTo(20);
        await Assert.That(page.Items.Count).IsEqualTo(Math.Min(20, page.TotalCount));
    }

    [Test]
    public async Task List_PagesThroughMessages()
    {
        var client = target.CreateAuthenticatedClient();
        await client.CreateMessageAsync("page-a");
        await client.CreateMessageAsync("page-b");

        var first = (await client.GetFromJsonAsync<PageBody>("/api/messages?page=1&pageSize=1"))!;
        var second = (await client.GetFromJsonAsync<PageBody>("/api/messages?page=2&pageSize=1"))!;

        await Assert.That(first.Items).HasSingleItem();
        await Assert.That(first.TotalPages).IsEqualTo(first.TotalCount);
        await Assert.That(first.HasNextPage).IsTrue();
        // next.page follows the API's 1-based pages. (next.offset is computed as page * pageSize,
        // i.e. one page too far for 1-based pages; not asserted until that's settled.)
        await Assert.That(first.Next!.Page).IsEqualTo(2);
        await Assert.That(first.Next.PageSize).IsEqualTo(1);
        await Assert.That(second.PageNumber).IsEqualTo(2);
        await Assert.That(second.Items).HasSingleItem();
        // No cross-request comparison of items: the store has no stable order and other tests
        // add and delete messages concurrently.
    }

    [Test]
    public async Task List_PastTheLastPage_IsEmptyWithNoNextPage()
    {
        var page = (await target.CreateClient().GetFromJsonAsync<PageBody>("/api/messages?page=1000000&pageSize=1"))!;

        await Assert.That(page.PageNumber).IsEqualTo(1000000);
        await Assert.That(page.Items).IsEmpty();
        await Assert.That(page.HasNextPage).IsFalse();
        await Assert.That(page.Next).IsNull();
    }

    [Test]
    [Arguments("page=0", 1, 20)]
    [Arguments("page=-3", 1, 20)]
    [Arguments("pageSize=0", 1, 1)]
    [Arguments("pageSize=1000", 1, 100)]
    public async Task List_OutOfRangeValues_AreClamped(string query, int expectedPage, int expectedPageSize)
    {
        var page = await target.CreateClient().GetFromJsonAsync<PageBody>($"/api/messages?{query}");

        await Assert.That(page!.PageNumber).IsEqualTo(expectedPage);
        await Assert.That(page.PageSize).IsEqualTo(expectedPageSize);
    }

    [Test]
    public async Task List_NonNumericPageSize_Is400()
    {
        using var response = await target.CreateClient().GetAsync("/api/messages?pageSize=abc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
