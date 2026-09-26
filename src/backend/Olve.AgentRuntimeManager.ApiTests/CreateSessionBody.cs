namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>A create request on the fake provider (its <c>fake:</c> directives go in the prompt).</summary>
public sealed record CreateSessionBody(string Prompt, string Caller = "api-tests", int? TimeoutSeconds = null)
{
    public string Provider { get; init; } = "fake";

    public string Model { get; init; } = "fake";
}
