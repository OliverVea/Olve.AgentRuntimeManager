namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// A turn's <c>result</c> event: how the turn ended (<c>success</c>, <c>error_during_execution</c>,
/// <c>error_max_turns</c>, …), the agent's final text, and the errors of a failed turn.
/// </summary>
public sealed record ClaudeResult(string Subtype, bool IsError, string? Text, IReadOnlyList<string> Errors);
