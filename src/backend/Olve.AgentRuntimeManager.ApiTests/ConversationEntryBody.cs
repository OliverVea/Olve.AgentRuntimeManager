using System.Text.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>One conversation entry as the API returns it.</summary>
public sealed record ConversationEntryBody(
    int Seq,
    string Kind,
    DateTimeOffset? At,
    string? Text,
    string? Tool,
    JsonElement? Input,
    string? ToolId,
    bool? IsError,
    string? ParentToolId);
