using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Domain → contract.</summary>
public static class SessionMapping
{
    /// <param name="providerRetries">The server's <see cref="SessionOptions.ProviderRetries"/>, for <c>retriesLeft</c>.</param>
    public static Session ToDto(this SessionRecord session, int providerRetries) => new()
    {
        Id = session.Id,
        Status = session.Status,
        QueuePosition = session.QueuePosition,
        Prompt = session.Prompt,
        Provider = session.Provider,
        Model = session.Model,
        Caller = session.Caller,
        TimeoutSeconds = session.TimeoutSeconds,
        Attempts = session.Attempts,
        RetriesLeft = Math.Max(0, providerRetries - session.FailedAttempts),
        CreatedAt = session.CreatedAt,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        ProviderSessionId = session.ProviderSessionId,
        ExitCode = session.ExitCode,
        Error = session.Error,
        KillReason = session.KillReason,
        KillSource = session.KillSource,
        KillCaller = session.KillCaller,
    };
}
