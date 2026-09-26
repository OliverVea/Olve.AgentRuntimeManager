using Olve.Results;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// Maps a handler's <see cref="Result"/> onto the statuses the operation declares in the contract.
/// Success writes the value (or no body) with the declared success status. Failure writes the
/// problems with: the status of an <c>http:NNN</c> tag on a problem, if the operation declares
/// it; else 400 if declared; else the first declared error; else 500. The one place M4's error
/// envelope changes.
/// </summary>
public static class ArmResults
{
    /// <summary>The problem tag that asks for a specific declared status, e.g. <c>http:404</c>.</summary>
    public static string StatusTag(int status) => $"http:{status}";

    public static IResult Map<T>(Result<T> result, ArmOperation operation) =>
        result.TryPickProblems(out var problems, out var value)
            ? Problems(problems, operation)
            : TypedResults.Json(value, statusCode: operation.SuccessStatus);

    public static IResult Map(Result result, ArmOperation operation) =>
        result.TryPickProblems(out var problems)
            ? Problems(problems, operation)
            : TypedResults.StatusCode(operation.SuccessStatus);

    /// <summary>
    /// An SSE operation: the events as <c>text/event-stream</c> (<see cref="ArmServerSentEventsResult{T}"/>),
    /// or, when the handler fails before streaming, its problems mapped like any other operation's.
    /// </summary>
    public static IResult Stream<T>(Result<IAsyncEnumerable<ArmSseItem<T>>> result, ArmOperation operation)
        where T : IArmEvent =>
        result.TryPickProblems(out var problems, out var events)
            ? Problems(problems, operation)
            : new ArmServerSentEventsResult<T>(events);

    /// <summary>The problem body the contract declares for errors.</summary>
    public static IResult Problems(IEnumerable<ResultProblem> problems, int status) =>
        TypedResults.Json<IReadOnlyList<ResultProblem>>([.. problems], statusCode: status);

    public static int StatusFor(IEnumerable<ResultProblem> problems, ArmOperation operation)
    {
        var declared = operation.ErrorStatuses;
        foreach (var tag in problems.SelectMany(p => p.Tags ?? []))
        {
            if (tag.StartsWith("http:", StringComparison.Ordinal) &&
                int.TryParse(tag.AsSpan(5), out var status) &&
                declared.Contains(status))
            {
                return status;
            }
        }

        return declared.Contains(StatusCodes.Status400BadRequest) ? StatusCodes.Status400BadRequest
            : declared.Count > 0 ? declared[0]
            : StatusCodes.Status500InternalServerError;
    }

    private static IResult Problems(ResultProblemCollection problems, ArmOperation operation)
    {
        var list = problems.ToList();
        return Problems(list, StatusFor(list, operation));
    }
}
