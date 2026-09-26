namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// Answers request-binding failures (a <see cref="BadHttpRequestException"/>: a malformed or
/// incomplete JSON body, <c>pageSize=abc</c>, a missing required parameter) in the contract's
/// terms: when the matched operation declares the status, the body is the error envelope
/// (<c>INVALID_REQUEST</c>); otherwise the status alone, as ASP.NET Core would. Requires
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
            var error = ArmError.Create(ArmErrors.InvalidRequest, exception.Message);
            await ArmErrors.ToHttpResult(error, exception.StatusCode, operation).ExecuteAsync(context);
        }
    }
}
