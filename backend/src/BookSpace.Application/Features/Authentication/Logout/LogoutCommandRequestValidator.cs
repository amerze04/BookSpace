using FluentValidation;

namespace BookSpace.Application.Features.Authentication.Logout;

public sealed class LogoutCommandRequestValidator : AbstractValidator<LogoutCommandRequest>
{
    public LogoutCommandRequestValidator()
    {
        RuleFor(c => c.RefreshToken)
            .NotEmpty()
            .MaximumLength(512);
    }
}
