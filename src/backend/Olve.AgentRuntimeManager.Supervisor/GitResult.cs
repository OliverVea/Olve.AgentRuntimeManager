namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// How a git command ended: its exit code and what it wrote (trimmed), or that it ran out of time
/// (<see cref="TimedOut"/>) or was stopped (<see cref="Cancelled"/>, a kill); then the output is what it wrote until then.
/// </summary>
internal readonly record struct GitResult(int ExitCode, string Output, string Error, bool TimedOut, bool Cancelled = false)
{
    public bool Succeeded => !TimedOut && !Cancelled && ExitCode == 0;
}
