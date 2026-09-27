using System.Text.Json;
using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Conversations;

/// <summary>One entry of a session's conversation, as its provider reads it (numbered when served).</summary>
public sealed record ConversationEntryRecord(
    ConversationEntryKind Kind,
    string? Text = null,
    string? Tool = null,
    JsonElement? Input = null,
    string? ToolId = null,
    bool? IsError = null,
    string? ParentToolId = null,
    DateTimeOffset? At = null);
