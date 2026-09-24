using FluentValidation;

namespace BookSpace.Application.Features.Authentication.Activate;

// Shape only, as every validator here is. Whether the token is *real* is the
// handler's job, and it deliberately reports one undifferentiated failure — see
// AuthenticationFailureReason.InvalidActivationToken.
//
// **The ordering matters and is free.** ValidationBehavior runs before the
// handler, so a password that breaks the policy is refused as a 400 without the
// token ever being looked up. If it were the other way round, a valid token
// plus a short password would answer 400 while an invalid token answered 401,
// and the pair would tell an attacker which invitations are live — the exact
// oracle the single reason code exists to prevent.
public sealed class ActivateAccountCommandRequestValidator : AbstractValidator<ActivateAccountCommandRequest>
{
    public ActivateAccountCommandRequestValidator()
    {
        RuleFor(c => c.Token)
            .NotEmpty()
            // Comfortably above the 43 characters a 256-bit Base64Url token
            // actually takes, and low enough that nothing enormous reaches the
            // hash function on an anonymous endpoint.
            .MaximumLength(512);

        RuleFor(c => c.Password)
            .NotEmpty()
            .MinimumLength(PasswordPolicy.MinimumLength)
            .MaximumLength(PasswordPolicy.MaximumLength);
    }
}
