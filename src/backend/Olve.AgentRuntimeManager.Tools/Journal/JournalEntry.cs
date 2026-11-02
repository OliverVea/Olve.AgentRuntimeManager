namespace Olve.AgentRuntimeManager.Tools.Journal;

/// <summary>
/// One line of the journal: a call by its tool-use id, when it <c>started</c> (written before it
/// runs) or when it <c>finished</c> (with its result). No arguments: a file's content can be large,
/// and the agent's transcript has them. <paramref name="IsRaw"/>: the text is an external MCP
/// server's whole result as JSON (M10c), not the text of one.
/// </summary>
internal sealed record JournalEntry(string Id, string Event, string Tool, DateTimeOffset At, string? Text = null, bool? IsError = null, bool? IsRaw = null)
{
    public const string Started = "started";
    public const string Finished = "finished";
}
