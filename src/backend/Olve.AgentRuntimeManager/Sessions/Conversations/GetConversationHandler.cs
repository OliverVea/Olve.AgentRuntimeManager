using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Conversations;

/// <summary><c>GET /api/sessions/{id}/conversation</c>: the session's conversation, as its provider keeps it.</summary>
public sealed class GetConversationHandler(SessionManager sessions) : ISessionConversationGetHandler
{
    public Task<SessionConversationGetResponse> HandleAsync(SessionConversationGetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SessionConversationGetResponse>(sessions.Conversation(request.Id) is { } entries
            ? new SessionConversationGetResponse.Ok(entries.ToDto())
            : new SessionConversationGetResponse.NotFound(SessionErrors.NotFound(request.Id)));
}
