using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// <c>POST /api/sessions</c>: the new session's id, 201 when started, 202 when queued. Its agent's
/// environment is resolved here, from the registered variables and the request's own.
/// </summary>
public sealed class CreateSessionHandler(SessionManager sessions, IdempotencyStore<SessionsCreateResponse> idempotency, IEnvStore env) : ISessionsCreateHandler
{
    public Task<SessionsCreateResponse> HandleAsync(SessionsCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body.Prompt))
        {
            return Task.FromResult<SessionsCreateResponse>(new SessionsCreateResponse.BadRequest(SessionErrors.BlankPrompt()));
        }

        var response = idempotency.GetOrAct(
            request.IdempotencyKey,
            () => Create(request.Body),
            keep: r => r is SessionsCreateResponse.Created or SessionsCreateResponse.Accepted);
        return Task.FromResult(response);
    }

    private SessionsCreateResponse Create(CreateSession body)
    {
        var (agentEnv, problem) = SessionEnv.Resolve(body, env);
        if (problem is not null)
        {
            return new SessionsCreateResponse.BadRequest(problem);
        }

        return sessions.Create(body, agentEnv) switch
        {
            CreateOutcome.Started started => new SessionsCreateResponse.Created(new CreatedSession { Id = started.Session.Id }),
            CreateOutcome.Queued queued => new SessionsCreateResponse.Accepted(new CreatedSession { Id = queued.Session.Id }),
            CreateOutcome.QueueFull full => new SessionsCreateResponse.ServiceUnavailable(SessionErrors.QueueFull(full.MaxQueueSize)),
            CreateOutcome.UnknownProvider unknown => new SessionsCreateResponse.BadRequest(SessionErrors.UnknownProvider(unknown.Provider, unknown.Known)),
            _ => throw new InvalidOperationException("Unhandled create outcome."),
        };
    }
}
