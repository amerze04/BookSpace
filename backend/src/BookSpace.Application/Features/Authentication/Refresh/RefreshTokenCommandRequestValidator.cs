using FluentValidation;

namespace BookSpace.Application.Features.Authentication.Refresh;

public sealed class RefreshTokenCommandRequestValidator : AbstractValidator<RefreshTokenCommandRequest>
{
    public RefreshTokenCommandRequestValidator()
    {
        RuleFor(c => c.RefreshToken)
            .NotEmpty()
            .MaximumLength(512);
    }
}
