using Olve.Results;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// The errors the runtime itself answers with, before a handler runs: binding failures and
/// failed request validation (both <see cref="InvalidRequest"/>, 400 when the operation declares it).
/// </summary>
public static class ArmErrors
{
    /// <summary>Malformed request (a binding failure) or a value the contract rejects.</summary>
    public const string InvalidRequest = "INVALID_REQUEST";

    /// <summary>
    /// <see cref="InvalidRequest"/> from validation problems: the first one's message, and every
    /// one in <c>details.problems</c> when there are several.
    /// </summary>
    public static ArmError Invalid(IReadOnlyList<ResultProblem> problems)
    {
        var entries = problems.Select(p => new ArmErrorProblem(InvalidRequest, p.FormattedMessage)).ToList();
        var message = entries.FirstOrDefault()?.Message ?? "The request is invalid.";
        return new ArmError(InvalidRequest, message, new ArmErrorDetails { Problems = entries.Count > 1 ? entries : null });
    }
}
