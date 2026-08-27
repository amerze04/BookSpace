using FluentValidation;

namespace BookSpace.Application.Features.Authentication.Logout;

public sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
    {
        RuleFor(c => c.RefreshToken)
            .NotEmpty()
            .MaximumLength(512);
    }
}
