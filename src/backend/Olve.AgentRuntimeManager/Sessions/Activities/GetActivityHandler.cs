using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Activities;

/// <summary><c>GET /api/sessions/{id}/activity</c>: the latest lines of what the session's agent did (M5f).</summary>
public sealed class GetActivityHandler(SessionManager sessions) : ISessionActivityGetHandler
{
    public const int DefaultLimit = 3;
    public const int MaxLimit = 50;

    public Task<SessionActivityGetResponse> HandleAsync(SessionActivityGetRequest request, CancellationToken cancellationToken)
    {
        // The generated surface validates bodies, not query parameters: the contract's bounds are checked here.
        var limit = request.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
        {
            var problem = limit < 1 ? "'limit' must be at least 1." : $"'limit' cannot exceed {MaxLimit}.";
            return Task.FromResult<SessionActivityGetResponse>(
                new SessionActivityGetResponse.BadRequest(ArmError.Create(ArmErrors.InvalidRequest, problem)));
        }

        return Task.FromResult<SessionActivityGetResponse>(sessions.LatestConversation(request.Id) is var (session, newestFirst)
            ? new Activity { Lines = ActivityLines.Pick(session, newestFirst, limit) }
            : new SessionActivityGetResponse.NotFound(SessionErrors.NotFound(request.Id)));
    }
}
