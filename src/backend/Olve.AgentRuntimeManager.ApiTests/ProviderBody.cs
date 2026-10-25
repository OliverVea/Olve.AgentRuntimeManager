namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A provider and its models as <c>GET /api/providers</c> returns them.</summary>
public sealed record ProviderBody(string Name, string DisplayName, IReadOnlyList<ProviderModelBody> Models);
