namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>
/// What a one-off supervisor command (<c>skill-install</c>) ended with: its exit code, and what it
/// wrote to its standard output and error (trimmed). A command stopped at its timeout has
/// <see cref="TimedOut"/> set, an exit code of -1 and the reason as its <see cref="Error"/>.
/// </summary>
public sealed record SupervisorCommandResult(int ExitCode, string Output, string Error, bool TimedOut = false);
