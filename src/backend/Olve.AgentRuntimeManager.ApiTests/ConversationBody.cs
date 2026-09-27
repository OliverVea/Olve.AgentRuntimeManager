
namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A session's conversation as the API returns it.</summary>
public sealed record ConversationBody(ConversationEntryBody[] Entries, int Turns, int Messages, int ToolCalls, int LastSeq);
