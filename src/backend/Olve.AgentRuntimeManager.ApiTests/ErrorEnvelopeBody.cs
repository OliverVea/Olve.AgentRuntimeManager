namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>The error envelope: <c>{ "error": { code, message, details } }</c>.</summary>
public sealed record ErrorEnvelopeBody(ErrorBody Error);
