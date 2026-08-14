using System.Text.Json;
using Olve.MinimalApi;
using Olve.Results;
using Olve.AgentRuntimeManager.Stores;
using Olve.Utilities.AsyncOnStartup;
using Olve.Utilities.Ids;
using Olve.Utilities.Paginations;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.Messages;

/// <summary>
/// The template's one end-to-end CRUD feature, exercising <see cref="Id{T}"/>,
/// <c>EntityStore&lt;Message&gt;</c>, <see cref="Page{T}"/> pagination, the
/// <c>IHandler</c> + <c>WithValidation</c> pattern, and <see cref="IAsyncOnStartup"/> wiring.
/// </summary>
public static class MessageEndpoints
{
    private const string SnapshotKey = "messages.json";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static void AddMessageServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new EntityStore<Message>([]));
        services.AddSingleton<CreateMessageHandler>();
        services.AddSingleton<UpdateMessageHandler>();
        services.AddSingleton<DeleteMessageHandler>();
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

    public static void MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/messages", (EntityStore<Message> store, int page = 1, int pageSize = DefaultPageSize) =>
            {
                // The API exposes a 1-based page; Pagination.Page is 0-based (Offset = Page * PageSize).
                page = Math.Max(page, 1);
                pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
                var all = store.List();
                var pagination = new Pagination(page - 1, pageSize);
                var items = all.Skip(pagination.Offset).Take(pagination.PageSize).ToList();
                return Result<Page<Message>>.Success(new Page<Message>(items, page, pageSize, all.Count));
            })
            .WithName("GetMessages")
            .WithResultMapping<Page<Message>>()
            .AllowAnonymous();

        app.MapPost("/messages", (CreateMessageHandler handler, MessageRequest request, CancellationToken ct) =>
                handler.HandleAsync(request, ct))
            .WithName("CreateMessage")
            .WithValidation<MessageRequest, MessageRequestValidator>()
            .WithResultMapping<Message>();

        app.MapPut("/messages/{id}", (UpdateMessageHandler handler, Id<Message> id, MessageRequest request, CancellationToken ct) =>
                handler.HandleAsync(new UpdateMessageCommand(id, request.Text), ct))
            .WithName("UpdateMessage")
            .WithValidation<MessageRequest, MessageRequestValidator>()
            .WithResultMapping<Message>();

        app.MapDelete("/messages/{id}", (DeleteMessageHandler handler, Id<Message> id, CancellationToken ct) =>
                handler.RunAsync(id, ct))
            .WithName("DeleteMessage")
            .WithResultMapping();
    }
}
