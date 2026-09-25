using Olve.MinimalApi;
using Olve.Results;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;

namespace Olve.AgentRuntimeManager.Messages;

/// <summary>Creates a <see cref="Message"/> with a freshly generated <see cref="Id{T}"/>.</summary>
public sealed class CreateMessageHandler(EntityStore<Message> store) : IHandler<MessageRequest, Message>
{
    /// <inheritdoc />
    public Task<Result<Message>> HandleAsync(MessageRequest request, CancellationToken cancellationToken)
    {
        var message = new Message(Id.New<Message>(), request.Text);
        store.Set(message);
        return Task.FromResult<Result<Message>>(message);
    }
}

/// <summary>Looks up a <see cref="Message"/> by id, or returns a not-found problem.</summary>
public sealed class GetMessageHandler(EntityStore<Message> store) : IHandler<Id<Message>, Message>
{
    /// <inheritdoc />
    public Task<Result<Message>> HandleAsync(Id<Message> request, CancellationToken cancellationToken)
    {
        if (!store.TryGet(request, out var message))
        {
            return Task.FromResult<Result<Message>>(new ResultProblem("Message with id '{0}' was not found.", request));
        }

        return Task.FromResult<Result<Message>>(message);
    }
}

/// <summary>The handler input for an update — carries the route id alongside the validated body text.</summary>
public sealed record UpdateMessageCommand(Id<Message> Id, string Text);

/// <summary>Updates an existing <see cref="Message"/>, or returns the store's not-found problem.</summary>
public sealed class UpdateMessageHandler(EntityStore<Message> store) : IHandler<UpdateMessageCommand, Message>
{
    /// <inheritdoc />
    public Task<Result<Message>> HandleAsync(UpdateMessageCommand request, CancellationToken cancellationToken)
    {
        var mutation = store.Mutate(request.Id, message => message with { Text = request.Text });
        if (mutation.TryPickProblems(out var problems))
        {
            return Task.FromResult<Result<Message>>(problems);
        }

        store.TryGet(request.Id, out var updated);
        return Task.FromResult<Result<Message>>(updated!);
    }
}

/// <summary>
/// Deletes a <see cref="Message"/> by id. Uses the response-less <see cref="IHandler{TRequest}"/>
/// shape since a delete has nothing meaningful to return.
/// </summary>
public sealed class DeleteMessageHandler(EntityStore<Message> store) : IHandler<Id<Message>>
{
    /// <inheritdoc />
    public Task<Result> RunAsync(Id<Message> request, CancellationToken cancellationToken)
    {
        var deletion = store.Delete(request);
        if (deletion.WasNotFound)
        {
            return Task.FromResult<Result>(new ResultProblem("Message with id '{0}' was not found.", request));
        }

        return Task.FromResult(Result.Success());
    }
}
