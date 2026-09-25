using Olve.Results.TUnit;
using Olve.AgentRuntimeManager.Messages;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.UnitTests.Messages;

public class MessageHandlerTests
{
    private static Message Seed(EntityStore<Message> store, string text)
    {
        var message = new Message(Id.New<Message>(), text);
        store.Set(message);
        return message;
    }

    [Test]
    public async Task Create_StoresMessageWithGeneratedId()
    {
        var store = new EntityStore<Message>([]);
        var handler = new CreateMessageHandler(store);

        var result = await handler.HandleAsync(new MessageRequest("hello"), CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var created);
        await Assert.That(created!.Text).IsEqualTo("hello");
        await Assert.That(store.List().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Get_ExistingMessage_ReturnsIt()
    {
        var store = new EntityStore<Message>([]);
        var existing = Seed(store, "hello");
        var handler = new GetMessageHandler(store);

        var result = await handler.HandleAsync(existing.Id, CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var found);
        await Assert.That(found).IsEqualTo(existing);
    }

    [Test]
    public async Task Get_MissingMessage_Fails()
    {
        var store = new EntityStore<Message>([]);
        Seed(store, "other");
        var handler = new GetMessageHandler(store);

        var result = await handler.HandleAsync(Id.New<Message>(), CancellationToken.None);

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Update_ExistingMessage_ChangesText()
    {
        var store = new EntityStore<Message>([]);
        var existing = Seed(store, "before");
        var handler = new UpdateMessageHandler(store);

        var result = await handler.HandleAsync(new UpdateMessageCommand(existing.Id, "after"), CancellationToken.None);

        await Assert.That(result).Succeeded();
        result.TryPickValue(out var updated);
        await Assert.That(updated!.Text).IsEqualTo("after");
    }

    [Test]
    public async Task Update_MissingMessage_Fails()
    {
        var store = new EntityStore<Message>([]);
        var handler = new UpdateMessageHandler(store);

        var result = await handler.HandleAsync(new UpdateMessageCommand(Id.New<Message>(), "x"), CancellationToken.None);

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Delete_ExistingMessage_Succeeds()
    {
        var store = new EntityStore<Message>([]);
        var existing = Seed(store, "bye");
        var handler = new DeleteMessageHandler(store);

        var result = await handler.RunAsync(existing.Id, CancellationToken.None);

        await Assert.That(result).Succeeded();
        await Assert.That(store.Contains(existing.Id)).IsFalse();
    }

    [Test]
    public async Task Delete_MissingMessage_Fails()
    {
        var store = new EntityStore<Message>([]);
        var handler = new DeleteMessageHandler(store);

        var result = await handler.RunAsync(Id.New<Message>(), CancellationToken.None);

        await Assert.That(result).Failed();
    }
}
