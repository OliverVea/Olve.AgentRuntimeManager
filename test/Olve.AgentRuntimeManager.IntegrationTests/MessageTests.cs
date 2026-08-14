using System.Net;
using System.Text;
using Olve.AgentRuntimeManager.Client;

namespace Olve.AgentRuntimeManager.IntegrationTests;

[ClassDataSource<AppFixture>(Shared = SharedType.PerAssembly)]
public class MessageTests(AppFixture fixture)
{
    [Test]
    public async Task CreateMessage_Authenticated_ReturnsCreatedMessage()
    {
        var api = fixture.CreateApiClient();

        var created = await api.CreateMessage(new MessageRequest { Text = "hello" });

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
        var api = fixture.CreateApiClient();

        var act = async () => await api.CreateMessage(new MessageRequest { Text = "" });

        await Assert.That(act).Throws<Refit.ApiException>();
    }

    [Test]
    public async Task GetMessages_AfterCreate_IncludesCreatedMessage()
    {
        var api = fixture.CreateApiClient();
        var created = await api.CreateMessage(new MessageRequest { Text = "find-me" });

        var page = await api.GetMessages(null, null);

        await Assert.That(page.Items.Any(m => m.Id == created.Id && m.Text == "find-me")).IsTrue();
        await Assert.That(page.TotalCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task UpdateMessage_ExistingMessage_ChangesText()
    {
        var api = fixture.CreateApiClient();
        var created = await api.CreateMessage(new MessageRequest { Text = "before" });

        var updated = await api.UpdateMessage(created.Id.ToString(), new MessageRequest { Text = "after" });

        await Assert.That(updated.Id).IsEqualTo(created.Id);
        await Assert.That(updated.Text).IsEqualTo("after");
    }

    [Test]
    public async Task DeleteMessage_ExistingMessage_RemovesIt()
    {
        var api = fixture.CreateApiClient();
        var created = await api.CreateMessage(new MessageRequest { Text = "delete-me" });

        await api.DeleteMessage(created.Id.ToString());

        var page = await api.GetMessages(null, null);
        await Assert.That(page.Items.Any(m => m.Id == created.Id)).IsFalse();
    }
}
