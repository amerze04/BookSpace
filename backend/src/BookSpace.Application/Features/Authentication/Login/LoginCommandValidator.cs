using FluentValidation;

namespace BookSpace.Application.Features.Authentication.Login;

// Shape only. Whether the credentials are *correct* is the handler's job, and
// it deliberately reports one undifferentiated failure — see
// AuthenticationFailureReason.
public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(c => c.Email)
            .NotEmpty()
            .MaximumLength(320)
            .EmailAddress();

        RuleFor(c => c.Password)
            .NotEmpty();
    }
}
