using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>A provider whose agents end only when a test says so (or are killed).</summary>
public sealed class ControlledProvider(string name = "controlled") : IAgentProvider
{
    private readonly List<ControlledRun> _runs = [];

    public string Name => name;

    /// <summary>The one model the tests' sessions name.</summary>
    public IReadOnlyList<ModelOptions> Models { get; } = [new() { Id = "m" }];

    /// <summary>When set, <see cref="Start"/> throws this instead of starting.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>What <see cref="CheckAsync"/> finds.</summary>
    public AgentOutcome.Unavailable? CheckResult { get; set; }

    public Task<AgentOutcome.Unavailable?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(CheckResult);

    /// <summary>What <see cref="Recover"/> does; unset, every agent is lost.</summary>
    public Func<AgentRecovery, RecoveredAgent?>? Recovering { get; set; }

    /// <summary>What <see cref="Recover"/> was asked.</summary>
    public List<AgentRecovery> Recoveries { get; } = [];

    public RecoveredAgent? Recover(AgentRecovery recovery)
    {
        Recoveries.Add(recovery);
        return Recovering?.Invoke(recovery);
    }

    /// <summary>A recovered agent that ends only when a test says so (<see cref="Runs"/> has it).</summary>
    public RecoveredAgent Reattach(AgentRecovery recovery, bool resumed = false)
    {
        var run = new ControlledRun(new AgentLaunch(recovery.SessionId, recovery.Prompt, recovery.Model, 0, Guid.Empty, resumed ? recovery.ResumeRunId : recovery.RunId));
        lock (_runs)
        {
            _runs.Add(run);
        }

        return new RecoveredAgent(run, resumed);
    }

    public IReadOnlyList<ControlledRun> Runs
    {
        get
        {
            lock (_runs)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>The sessions whose worktrees <see cref="RemoveRepos"/> was asked to remove, with their repositories.</summary>
    public List<(Guid SessionId, IReadOnlyList<RepoRecord> Repos)> RemovedRepos { get; } = [];

    /// <summary>What a test does while the worktrees are being removed.</summary>
    public Action<Guid>? Removing { get; set; }

    /// <summary>Whether removing worktrees fails (they stay, to be tried again).</summary>
    public bool RemovalFails { get; set; }

    public IReadOnlyList<RepoRecord> RemoveRepos(Guid sessionId, IReadOnlyList<RepoRecord> repos)
    {
        Removing?.Invoke(sessionId);
        RemovedRepos.Add((sessionId, repos));
        return RemovalFails ? repos : [];
    }

    public ControlledRun RunOf(Guid sessionId) => Runs.Single(r => r.Launch.SessionId == sessionId);

    public IAgentRun Start(AgentLaunch launch)
    {
        if (StartFailure is { } failure)
        {
            throw failure;
        }

        var run = new ControlledRun(launch);
        lock (_runs)
        {
            _runs.Add(run);
        }

        return run;
    }
}
