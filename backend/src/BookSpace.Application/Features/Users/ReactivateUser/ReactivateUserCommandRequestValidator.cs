using FluentValidation;

namespace BookSpace.Application.Features.Users.ReactivateUser;

public sealed class ReactivateUserCommandRequestValidator
    : AbstractValidator<ReactivateUserCommandRequest>
{
    public ReactivateUserCommandRequestValidator()
    {
        RuleFor(c => c.UserId)
            .NotEmpty()
            .WithMessage("UserId is required.");
    }
}
