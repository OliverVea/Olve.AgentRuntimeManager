namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// One declared response of an operation: a variant of the generated per-operation response
/// union (<c>SessionsKillResponse.Conflict</c>, …) that knows its status and how to write itself.
/// </summary>
public interface IArmResponse
{
    /// <summary>The declared status of this variant (<c>200</c>, <c>404</c>, …).</summary>
    int Status { get; }

    /// <summary>Writes the variant with its status (and its body, if it has one).</summary>
    IResult ToHttpResult();
}
