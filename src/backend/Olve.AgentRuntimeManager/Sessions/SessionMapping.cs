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
        Env = session.Env,
        UseEnv = session.UseEnv,
        // As stored: the folder and branch it was created with, which the checkout uses. A repo of
        // a corrupt row that couldn't be named (StoredRepoNames) has neither and is left out.
        Repos = session.Repos?.Where(r => r.Folder is not null && r.Branch is not null)
            .Select(r => new SessionRepo { Url = r.Url, Ref = r.Ref, Folder = r.Folder, Branch = r.Branch }).ToList(),
        Tools = session.Tools,
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
