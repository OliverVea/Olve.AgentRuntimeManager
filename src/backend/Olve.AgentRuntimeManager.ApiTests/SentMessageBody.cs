namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>What <c>POST /api/sessions/{id}/messages</c> answers: what became of the message.</summary>
public sealed record SentMessageBody(string Delivery);
