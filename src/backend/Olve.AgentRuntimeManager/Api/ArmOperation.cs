namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// One contract operation as the generated code describes it (see the generated
/// <c>ArmOperations</c> table): its id, its success status and the error statuses it declares.
/// Attached to each generated endpoint as metadata, so middleware can answer in the contract's
/// terms (e.g. <see cref="ArmBindingFailures"/>).
/// </summary>
public sealed record ArmOperation(string OperationId, int SuccessStatus, IReadOnlyList<int> ErrorStatuses)
{
    public bool Declares(int status) => SuccessStatus == status || ErrorStatuses.Contains(status);
}
