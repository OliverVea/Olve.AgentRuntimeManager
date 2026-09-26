using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>POST /api/sessions/search</c>: one page of matching sessions, newest first.</summary>
public sealed class SearchSessionsHandler(SessionManager sessions) : ISessionsSearchHandler
{
    public const int DefaultLimit = 20;

    public Task<SessionsSearchResponse> HandleAsync(SessionsSearchRequest request, CancellationToken cancellationToken)
    {
        // limit (1–100) and offset (≥ 0) are checked by the generated validator.
        var page = sessions.Search(request.Body, request.Body.Limit ?? DefaultLimit, request.Body.Offset ?? 0);
        return Task.FromResult<SessionsSearchResponse>(new SessionPage
        {
            Items = [.. page.Items.Select(s => s.ToDto())],
            Total = page.Total,
            Limit = page.Limit,
            Offset = page.Offset,
        });
    }
}
