using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// <c>POST /api/sessions/{id}/messages</c> (M11): what became of the message. A message held
/// because the working session's agent could no longer take it is <c>continued</c>: the session
/// continues with it once that agent has ended.
/// </summary>
public sealed class SendMessageHandler(SessionManager sessions) : ISessionMessagesSendHandler
{
    public Task<SessionMessagesSendResponse> HandleAsync(SessionMessagesSendRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body.Text))
        {
            return Task.FromResult<SessionMessagesSendResponse>(new SessionMessagesSendResponse.BadRequest(SessionErrors.BlankMessage()));
        }

        return Task.FromResult<SessionMessagesSendResponse>(sessions.Send(request.Id, request.Body.Text, request.Body.Caller) switch
        {
            MessageOutcome.Delivered => Sent(MessageDelivery.Delivered),
            MessageOutcome.Pending => Sent(MessageDelivery.Pending),
            MessageOutcome.Continued or MessageOutcome.Held => Sent(MessageDelivery.Continued),
            MessageOutcome.NotFound => new SessionMessagesSendResponse.NotFound(SessionErrors.NotFound(request.Id)),
            MessageOutcome.NeverStarted never => new SessionMessagesSendResponse.Conflict(SessionErrors.NeverStarted(never.Session)),
            MessageOutcome.UnknownProvider unknown => new SessionMessagesSendResponse.Conflict(SessionErrors.ProviderGone(unknown.Session)),
            _ => throw new InvalidOperationException("Unhandled message outcome."),
        });
    }

    private static SessionMessagesSendResponse Sent(MessageDelivery delivery) => new SentMessage { Delivery = delivery };
}
