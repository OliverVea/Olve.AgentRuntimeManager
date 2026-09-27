namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// A working session's agent, found again after a restart: re-attached (or already ended), or,
/// when <paramref name="Resumed"/>, a new run (<see cref="AgentRecovery.ResumeRunId"/>) resuming it.
/// </summary>
public sealed record RecoveredAgent(IAgentRun Run, bool Resumed);
