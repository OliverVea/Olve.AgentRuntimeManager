using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Events;
using Olve.Results;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;
using Pages = Olve.Utilities.Paginations;

namespace Olve.AgentRuntimeManager.Messages;

// Handlers for the generated Messages_* operation interfaces (artifacts/generated/backend). They
// speak the generated DTOs (Api.Message, Api.MessageWritable, …) at the edge and the domain
// Message (with its Id<Message>) against the store. Every change is published on the EventBus
// (message.created / message.updated / message.deleted on GET /api/events).

/// <summary><c>GET /api/messages</c>: one 1-based page of messages; out-of-range values are clamped.</summary>
public sealed class ListMessagesHandler(EntityStore<Message> store) : IMessagesListHandler
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public Task<Result<PageOfMessage>> HandleAsync(MessagesListRequest request, CancellationToken cancellationToken)
    {
        // The API exposes a 1-based page; Pagination.Page is 0-based (Offset = Page * PageSize).
        var pageNumber = Math.Max(request.Page ?? 1, 1);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var all = store.List();
        var pagination = new Pages.Pagination(pageNumber - 1, pageSize);
        var items = all.Skip(pagination.Offset).Take(pagination.PageSize).ToList();
        var page = new Pages.Page<Message>(items, pageNumber, pageSize, all.Count);

        return Task.FromResult<Result<PageOfMessage>>(new PageOfMessage
        {
            Items = [.. page.Items.Select(MessageMapping.ToDto)],
            PageNumber = page.PageNumber,
            PageSize = page.PageSize,
            TotalCount = page.TotalCount,
            TotalPages = page.TotalPages,
            HasNextPage = page.HasNextPage,
            Next = page.Next is { } next
                ? new Api.Pagination { Page = next.Page, PageSize = next.PageSize, Offset = next.Offset }
                : null,
        });
    }
}

/// <summary><c>POST /api/messages</c>: creates a message with a freshly generated id.</summary>
public sealed class CreateMessageHandler(EntityStore<Message> store, EventBus events) : IMessagesCreateHandler
{
    public Task<Result<Api.Message>> HandleAsync(MessagesCreateRequest request, CancellationToken cancellationToken)
    {
        if (MessageMapping.ValidateText(request.Body.Text).TryPickProblems(out var problems))
        {
            return Task.FromResult<Result<Api.Message>>(problems);
        }

        var message = new Message(Id.New<Message>(), request.Body.Text);
        store.Set(message);
        var dto = message.ToDto();
        events.Publish(new MessageCreated { At = events.Now, MessageId = dto.Id, Message = dto });
        return Task.FromResult<Result<Api.Message>>(dto);
    }
}

/// <summary><c>GET /api/messages/{id}</c>: one message, or a 404 problem.</summary>
public sealed class GetMessageHandler(EntityStore<Message> store) : IMessagesGetHandler
{
    public Task<Result<Api.Message>> HandleAsync(MessagesGetRequest request, CancellationToken cancellationToken)
    {
        if (!Id.TryParse<Message>(request.Id, out var id) || !store.TryGet(id, out var message))
        {
            return Task.FromResult<Result<Api.Message>>(MessageMapping.NotFound(request.Id));
        }

        return Task.FromResult<Result<Api.Message>>(message.ToDto());
    }
}

/// <summary><c>PUT /api/messages/{id}</c>: replaces a message's text.</summary>
public sealed class UpdateMessageHandler(EntityStore<Message> store, EventBus events) : IMessagesUpdateHandler
{
    public Task<Result<Api.Message>> HandleAsync(MessagesUpdateRequest request, CancellationToken cancellationToken)
    {
        if (MessageMapping.ValidateText(request.Body.Text).TryPickProblems(out var problems))
        {
            return Task.FromResult<Result<Api.Message>>(problems);
        }

        if (!Id.TryParse<Message>(request.Id, out var id))
        {
            return Task.FromResult<Result<Api.Message>>(MessageMapping.NotFound(request.Id));
        }

        var mutation = store.Mutate(id, message => message with { Text = request.Body.Text });
        if (mutation.TryPickProblems(out problems))
        {
            return Task.FromResult<Result<Api.Message>>(problems);
        }

        store.TryGet(id, out var updated);
        var dto = updated!.ToDto();
        events.Publish(new MessageUpdated { At = events.Now, MessageId = dto.Id, Message = dto });
        return Task.FromResult<Result<Api.Message>>(dto);
    }
}

/// <summary><c>DELETE /api/messages/{id}</c>: deletes a message (no response body).</summary>
public sealed class DeleteMessageHandler(EntityStore<Message> store, EventBus events) : IMessagesDeleteHandler
{
    public Task<Result> RunAsync(MessagesDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!Id.TryParse<Message>(request.Id, out var id) || store.Delete(id).WasNotFound)
        {
            return Task.FromResult<Result>(MessageMapping.NotFound(request.Id));
        }

        events.Publish(new MessageDeleted { At = events.Now, MessageId = id.Value.Value });
        return Task.FromResult(Result.Success());
    }
}

/// <summary>Domain ↔ contract mapping and the rules the spec can't express.</summary>
public static class MessageMapping
{
    public static Api.Message ToDto(this Message message) =>
        new() { Id = message.Id.Value.Value, Text = message.Text };

    /// <summary>A missing (or malformed) id. Tagged 404: operations that declare it answer 404.</summary>
    public static ResultProblem NotFound(string id) =>
        new("Message with id '{0}' was not found.", id) { Tags = [ArmResults.StatusTag(StatusCodes.Status404NotFound)] };

    /// <summary>
    /// Non-blank text. The spec's <c>@maxLength(280)</c> (and presence) are checked by the
    /// generated validator before the handler runs; blankness has no TypeSpec decorator.
    /// </summary>
    public static Result ValidateText(string text) =>
        string.IsNullOrWhiteSpace(text) ? new ResultProblem("'text' cannot be empty.") : Result.Success();
}
