using Olve.Results;
using Olve.Validation;
using Olve.Validation.Validators;

namespace Olve.AgentRuntimeManager.Messages;

/// <summary>The request body for creating or updating a <see cref="Message"/>.</summary>
public sealed record MessageRequest(string Text);

/// <summary>
/// Validates a <see cref="MessageRequest"/>. Wired into the endpoints with
/// <c>.WithValidation&lt;MessageRequest, MessageRequestValidator&gt;()</c> so a failed validation
/// short-circuits to a 400 before the handler runs.
/// </summary>
public sealed class MessageRequestValidator : IValidator<MessageRequest>
{
    private const int MaxLength = 280;

    /// <inheritdoc />
    public Result Validate(MessageRequest value) =>
        new StringValidator()
            .CannotBeNullOrWhiteSpace()
            .WithMessage("'text' cannot be empty.")
            .MustHaveMaxLength(MaxLength)
            .WithMessage($"'text' cannot exceed {MaxLength} characters.")
            .Validate(value.Text);
}
