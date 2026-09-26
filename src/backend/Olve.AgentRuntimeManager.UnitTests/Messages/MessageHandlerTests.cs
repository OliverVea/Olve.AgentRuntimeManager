using Olve.Results.TUnit;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Messages;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;
using Message = Olve.AgentRuntimeManager.Messages.Message;

namespace Olve.AgentRuntimeManager.UnitTests.Messages;

public class MessageHandlerTests
{
    private static Message Seed(EntityStore<Message> store, string text)
    {
        var message = new Message(Id.New<Message>(), text);
        store.Set(message);
        return message;
    }

    private static MessageWritable Body(string text) => new() { Text = text };

    [Test]
    public async Task List_ClampsAndPages()
    {
        var store = new EntityStore<Message>([]);
        Seed(store, "a");
        Seed(store, "b");
        var handler = new ListMessagesHandler(store);

        var result = await handler.HandleAsync(new MessagesListRequest(Page: 0, PageSize: 1), CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var page);
        await Assert.That(page!.PageNumber).IsEqualTo(1);
        await Assert.That(page.Items.Count).IsEqualTo(1);
        await Assert.That(page.TotalCount).IsEqualTo(2);
        await Assert.That(page.TotalPages).IsEqualTo(2);
    }

    [Test]
    public async Task List_Defaults_LastPageHasNoNext()
    {
        var store = new EntityStore<Message>([]);
        Seed(store, "a");
        var handler = new ListMessagesHandler(store);

        var result = await handler.HandleAsync(new MessagesListRequest(null, null), CancellationToken.None);

        result.TryPickValue(out var page);
        await Assert.That(page!.PageSize).IsEqualTo(ListMessagesHandler.DefaultPageSize);
        await Assert.That(page.HasNextPage == false).IsTrue();
        await Assert.That(page.Next).IsNull();
    }

    [Test]
    public async Task Create_StoresMessageWithGeneratedId()
    {
        var store = new EntityStore<Message>([]);
        var handler = new CreateMessageHandler(store);

        var result = await handler.HandleAsync(new MessagesCreateRequest(Body("hello")), CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var created);
        await Assert.That(created!.Text).IsEqualTo("hello");
        await Assert.That(created.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(store.List().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Create_BlankText_Fails()
    {
        var store = new EntityStore<Message>([]);
        var handler = new CreateMessageHandler(store);

        var result = await handler.HandleAsync(new MessagesCreateRequest(Body("  ")), CancellationToken.None);

        await Assert.That(result).Failed();
        await Assert.That(store.List().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Get_ExistingMessage_ReturnsIt()
    {
        var store = new EntityStore<Message>([]);
        var existing = Seed(store, "hello");
        var handler = new GetMessageHandler(store);

        var result = await handler.HandleAsync(new MessagesGetRequest(existing.Id.ToString()), CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var found);
        await Assert.That(found).IsEqualTo(existing.ToDto());
    }

    [Test]
    [Arguments("0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11")]
    [Arguments("not-a-guid")]
    public async Task Get_MissingMessage_FailsWith404Tag(string id)
    {
        var store = new EntityStore<Message>([]);
        Seed(store, "other");
        var handler = new GetMessageHandler(store);

        var result = await handler.HandleAsync(new MessagesGetRequest(id), CancellationToken.None);

        await Assert.That(result).Failed();
        result.TryPickProblems(out var problems);
        await Assert.That(ArmResults.StatusFor(problems!, ArmOperations.MessagesGet)).IsEqualTo(404);
    }

    [Test]
    public async Task Update_ExistingMessage_ChangesText()
    {
        var store = new EntityStore<Message>([]);
        var existing = Seed(store, "before");
        var handler = new UpdateMessageHandler(store);

        var result = await handler.HandleAsync(new MessagesUpdateRequest(existing.Id.ToString(), Body("after")), CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var updated);
        await Assert.That(updated!.Text).IsEqualTo("after");
    }

    [Test]
    public async Task Update_MissingMessage_FailsAs400()
    {
        var store = new EntityStore<Message>([]);
        var handler = new UpdateMessageHandler(store);

        var result = await handler.HandleAsync(new MessagesUpdateRequest(Guid.NewGuid().ToString(), Body("x")), CancellationToken.None);

        await Assert.That(result).Failed();
        result.TryPickProblems(out var problems);
        // `update` declares no 404, so the not-found tag falls back to its 400.
        await Assert.That(ArmResults.StatusFor(problems!, ArmOperations.MessagesUpdate)).IsEqualTo(400);
    }

    [Test]
    public async Task Delete_ExistingMessage_Succeeds()
    {
        var store = new EntityStore<Message>([]);
        var existing = Seed(store, "bye");
        var handler = new DeleteMessageHandler(store);

        var result = await handler.RunAsync(new MessagesDeleteRequest(existing.Id.ToString()), CancellationToken.None);

        await Assert.That(result).Succeeded();
        await Assert.That(store.Contains(existing.Id)).IsFalse();
    }

    [Test]
    public async Task Delete_MissingMessage_Fails()
    {
        var store = new EntityStore<Message>([]);
        var handler = new DeleteMessageHandler(store);

        var result = await handler.RunAsync(new MessagesDeleteRequest(Guid.NewGuid().ToString()), CancellationToken.None);

        await Assert.That(result).Failed();
    }
}
