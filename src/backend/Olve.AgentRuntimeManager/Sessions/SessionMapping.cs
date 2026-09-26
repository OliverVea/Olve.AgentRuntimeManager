using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Domain → contract.</summary>
public static class SessionMapping
{
    public static Session ToDto(this SessionRecord session) => new()
    {
        Id = session.Id,
        Status = session.Status,
        QueuePosition = session.QueuePosition,
        Prompt = session.Prompt,
        Provider = session.Provider,
        Model = session.Model,
        Caller = session.Caller,
        TimeoutSeconds = session.TimeoutSeconds,
        CreatedAt = session.CreatedAt,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        ProviderSessionId = session.ProviderSessionId,
        ExitCode = session.ExitCode,
        Summary = session.Summary,
        Error = session.Error,
        KillReason = session.KillReason,
        KillSource = session.KillSource,
        KillCaller = session.KillCaller,
    };
}
