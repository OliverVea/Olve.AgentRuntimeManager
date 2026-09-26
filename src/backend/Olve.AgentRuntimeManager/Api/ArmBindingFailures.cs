using Olve.Results;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// Answers request-binding failures (a <see cref="BadHttpRequestException"/>: a malformed or
/// incomplete JSON body, <c>pageSize=abc</c>, a missing required parameter) in the contract's
/// terms: when the matched operation declares a 400, the body is the contract's problem array;
/// otherwise the status alone, as ASP.NET Core would. Requires
/// <c>RouteHandlerOptions.ThrowOnBadRequest</c> so the failures reach this middleware.
/// </summary>
public sealed class ArmBindingFailures(RequestDelegate next, ILogger<ArmBindingFailures> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            logger.LogDebug(exception, "Request binding failed on {Path}", context.Request.Path);

            var operation = context.GetEndpoint()?.Metadata.GetMetadata<ArmOperation>();
            context.Response.Clear();
            if (exception.StatusCode == StatusCodes.Status400BadRequest && operation?.Declares(StatusCodes.Status400BadRequest) == true)
            {
                var problem = new ResultProblem("{0}", exception.Message);
                await ArmResults.Problems([problem], StatusCodes.Status400BadRequest).ExecuteAsync(context);
                return;
            }

            context.Response.StatusCode = exception.StatusCode;
        }
    }
}
