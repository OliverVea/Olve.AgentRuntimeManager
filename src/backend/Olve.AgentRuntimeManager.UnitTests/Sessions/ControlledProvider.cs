using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>A provider whose agents end only when a test says so (or are killed).</summary>
public sealed class ControlledProvider(string name = "controlled") : IAgentProvider
{
    private readonly List<ControlledRun> _runs = [];

    public string Name => name;

    /// <summary>When set, <see cref="Start"/> throws this instead of starting.</summary>
    public Exception? StartFailure { get; set; }

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
