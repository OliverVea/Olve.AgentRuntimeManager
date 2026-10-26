namespace Olve.AgentRuntimeManager.Tools.Journal;

/// <summary>Where a call by its tool-use id stands in the journal (its result is in the file, not in memory).</summary>
internal enum CallState
{
    /// <summary>Started in this server and not ended yet.</summary>
    Running,

    /// <summary>Started and never finished: a server stopped while it ran.</summary>
    Interrupted,

    /// <summary>Finished, with its result in the journal.</summary>
    Finished,
}
