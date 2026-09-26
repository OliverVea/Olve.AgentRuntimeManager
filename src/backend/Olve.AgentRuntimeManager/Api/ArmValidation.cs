using Olve.Validation;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>
/// Runs a generated request-body validator (from the spec's <c>@maxLength</c> etc.) before the
/// handler; a failure is a 400 <see cref="ArmErrors.InvalidRequest"/>, like a binding failure.
/// </summary>
public static class ArmValidation
{
    public static RouteHandlerBuilder WithArmValidation<TRequest, TValidator>(this RouteHandlerBuilder builder, ArmOperation operation)
        where TValidator : IValidator<TRequest>, new()
    {
        var validator = new TValidator();
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
            if (request is not null && validator.Validate(request).TryPickProblems(out var problems))
            {
                return ArmErrors.ToHttpResult(ArmErrors.Invalid([.. problems]), StatusCodes.Status400BadRequest, operation);
            }

            return await next(context);
        });
    }
}
