using System.Text.Json;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Stores;
using Olve.Utilities.AsyncOnStartup;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.Messages;

/// <summary>
/// The template's one end-to-end CRUD feature: the generated <c>Messages_*</c> operations
/// (<see cref="ArmApi"/>) implemented against an <c>EntityStore&lt;Message&gt;</c> with
/// optional snapshot persistence and a startup seeder (<see cref="IAsyncOnStartup"/>).
/// </summary>
public static class MessageServices
{
    private const string SnapshotKey = "messages.json";

    public static void AddMessageServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new EntityStore<Message>([]));
        services.AddSingleton<IMessagesListHandler, ListMessagesHandler>();
        services.AddSingleton<IMessagesGetHandler, GetMessageHandler>();
        services.AddSingleton<IMessagesCreateHandler, CreateMessageHandler>();
        services.AddSingleton<IMessagesUpdateHandler, UpdateMessageHandler>();
        services.AddSingleton<IMessagesDeleteHandler, DeleteMessageHandler>();
        services.AddSingleton<IAsyncOnStartup, MessageSeeder>();

        // In-memory by default. Set "Storage:Mode" to "Persistent" (and optionally "Storage:Directory")
        // to persist across restarts via the BCL-only FileSnapshotStore — or swap in another
        // ISnapshotStore (S3, SQLite, …) here without touching the store or handlers.
        var mode = configuration.GetValue("Storage:Mode", StorageMode.Ephemeral);
        if (mode == StorageMode.Persistent)
        {
            var directory = configuration.GetValue<string>("Storage:Directory") ?? "data";
            services.AddSingleton<ISnapshotStore>(new FileSnapshotStore(directory));
        }

        services.AddEntityStorePersistence<Message>(
            key: SnapshotKey,
            serialize: messages => JsonSerializer.SerializeToUtf8Bytes(messages, AppJsonContext.Default.IReadOnlyListMessage),
            deserialize: bytes => JsonSerializer.Deserialize(bytes, AppJsonContext.Default.IReadOnlyListMessage),
            mode: mode);
    }
}
