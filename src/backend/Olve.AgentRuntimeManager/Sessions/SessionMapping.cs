using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Domain → contract. <see cref="SessionRecord.SecretEnv"/> is deliberately never mapped.</summary>
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
        Effort = session.Effort,
        SystemPrompt = session.SystemPrompt,
        Caller = session.Caller,
        Tags = session.Tags,
        Env = session.Env,
        TimeoutSeconds = session.TimeoutSeconds,
        ApprovalPolicy = session.ApprovalPolicy,
        Tools = session.Tools,
        Skills = session.Skills,
        Messaging = session.Messaging,
        Headless = session.Headless,
        CreatedAt = session.CreatedAt,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        ProviderSessionId = session.ProviderSessionId,
        ExitCode = session.ExitCode,
        Summary = session.Summary,
        Error = session.Error,
        KillReason = session.KillReason,
        KillSource = session.KillSource,
    };
}
