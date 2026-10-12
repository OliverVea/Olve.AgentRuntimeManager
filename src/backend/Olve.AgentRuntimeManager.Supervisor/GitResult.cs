namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>How a git command ended: its exit code and output (trimmed), or that it ran out of time.</summary>
internal readonly record struct GitResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
