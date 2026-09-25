using Olve.Utilities.AsyncOnStartup;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.Messages;

/// <summary>
/// Seeds a welcome message on first run so a freshly deployed template has something to return.
/// Demonstrates <see cref="IAsyncOnStartup"/> — the template's convention for one-shot startup work
/// (run after the persister has loaded, so it only seeds when the store is genuinely empty).
/// </summary>
public sealed class MessageSeeder(EntityStore<Message> store) : IAsyncOnStartup
{
    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public Task OnStartupAsync(CancellationToken cancellationToken)
    {
        if (store.List().Count == 0)
        {
            store.Set(new Message(Id.New<Message>(), "Welcome to Olve.AgentRuntimeManager!"));
        }

        return Task.CompletedTask;
    }
}
