namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// One contract operation as the generated code describes it (see the generated
/// <c>ArmOperations</c> table): its id, its success statuses (usually one; e.g. <c>201, 202</c> for
/// a create that may queue) and the error statuses it declares. Attached to each generated
/// endpoint as metadata, so middleware can answer in the contract's terms (e.g.
/// <see cref="ArmBindingFailures"/>).
/// </summary>
public sealed record ArmOperation(string OperationId, IReadOnlyList<int> SuccessStatuses, IReadOnlyList<int> ErrorStatuses)
{
    /// <summary>The first (for most operations, the only) declared success status.</summary>
    public int SuccessStatus => SuccessStatuses[0];

    public bool Declares(int status) => SuccessStatuses.Contains(status) || ErrorStatuses.Contains(status);
}
