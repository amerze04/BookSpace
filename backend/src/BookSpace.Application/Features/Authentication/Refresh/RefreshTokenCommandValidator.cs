using FluentValidation;

namespace BookSpace.Application.Features.Authentication.Refresh;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(c => c.RefreshToken)
            .NotEmpty()
            .MaximumLength(512);
    }
}
