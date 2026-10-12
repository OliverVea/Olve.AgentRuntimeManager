namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>
/// Every half minute, tries again to remove deleted sessions' worktrees that couldn't be removed
/// when they were deleted (<see cref="SessionManager.RetryWorktreeRemovals"/>, M5e). Kept in the
/// database, so a restart picks them up again.
/// </summary>
public sealed class WorktreeRemovalRetries(SessionManager sessions, TimeProvider time, ILogger<WorktreeRemovalRetries> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // It runs git (bounded) as the agent user: off the timer's thread.
                    await Task.Run(sessions.RetryWorktreeRemovals, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Retrying worktree removals failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }
}
