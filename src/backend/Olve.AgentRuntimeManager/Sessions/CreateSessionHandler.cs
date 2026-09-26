using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary><c>POST /api/sessions</c>: 201 when started, 202 when queued (with <c>queuePosition</c>).</summary>
public sealed class CreateSessionHandler(SessionManager sessions, IdempotencyStore<SessionsCreateResponse> idempotency) : ISessionsCreateHandler
{
    public Task<SessionsCreateResponse> HandleAsync(SessionsCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body.Prompt))
        {
            return Task.FromResult<SessionsCreateResponse>(new SessionsCreateResponse.BadRequest(SessionErrors.BlankPrompt()));
        }

        var response = idempotency.GetOrAct(
            request.IdempotencyKey,
            () => sessions.Create(request.Body) switch
            {
                CreateOutcome.Started started => new SessionsCreateResponse.Created(started.Session.ToDto()),
                CreateOutcome.Queued queued => new SessionsCreateResponse.Accepted(queued.Session.ToDto()),
                CreateOutcome.QueueFull full => new SessionsCreateResponse.ServiceUnavailable(SessionErrors.QueueFull(full.MaxQueueSize)),
                CreateOutcome.UnknownProvider unknown => new SessionsCreateResponse.BadRequest(SessionErrors.UnknownProvider(unknown.Provider, unknown.Known)),
                _ => throw new InvalidOperationException("Unhandled create outcome."),
            },
            keep: r => r is SessionsCreateResponse.Created or SessionsCreateResponse.Accepted);
        return Task.FromResult(response);
    }
}
