using Olve.Results;
using Olve.TypeSpec;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// Answers binding and validation failures in the contract's terms: the error envelope when the
/// matched operation declares the status, else the status alone, as ASP.NET Core would.
/// </summary>
public sealed class ArmErrorWriter : IApiErrorWriter
{
    public IResult InvalidRequest(IReadOnlyList<ResultProblem> problems, int status, ApiOperation? operation) =>
        operation?.Declares(status) == true
            ? TypedResults.Json(new ArmErrorEnvelope(ArmErrors.Invalid(problems)), ArmErrorJsonContext.Default.ArmErrorEnvelope, statusCode: status)
            : TypedResults.StatusCode(status);
}
