using FluentValidation;

namespace BookSpace.Application.Features.Users.DeactivateUser;

// Guid.Empty is a malformed request, not a lookup that misses — same reasoning
// as ArchiveResourceCommandRequestValidator.
public sealed class DeactivateUserCommandRequestValidator
    : AbstractValidator<DeactivateUserCommandRequest>
{
    public DeactivateUserCommandRequestValidator()
    {
        RuleFor(c => c.UserId)
            .NotEmpty()
            .WithMessage("UserId is required.");
    }
}
