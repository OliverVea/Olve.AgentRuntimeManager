namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A provider's health as the API returns it.</summary>
public sealed record ProviderHealthBody(
    string Provider,
    string Status,
    string? Reason,
    DateTimeOffset? Since,
    DateTimeOffset? Until);
