using FluentValidation;

namespace BookSpace.Application.Features.Users.CreateUser;

// Shape only. Whether the address is already taken is decided by
// UQ_Users_Email on the insert, not here — see IUserRepository.SaveChangesAsync
// for why there is deliberately no pre-check.
public sealed class CreateUserCommandRequestValidator : AbstractValidator<CreateUserCommandRequest>
{
    public CreateUserCommandRequestValidator()
    {
        RuleFor(c => c.Email)
            .NotEmpty()
            // Matches Users.Email's column width; anything longer cannot be
            // stored, and a 400 naming the field beats a truncation or a 500.
            .MaximumLength(320)
            // The same rule LoginCommandRequestValidator applies, so an address
            // this endpoint accepts is one that endpoint can be given back.
            .EmailAddress()
            // Narrower than .EmailAddress() on purpose, and on evidence rather
            // than taste. FluentValidation's default mode is deliberately
            // lenient — it checks little more than a single interior '@', so
            // "ada lovelace@acme.test" passes it — while MimeKit refuses an
            // address with a space outright (measured against 4.18, see
            // EmailAddressRules). Left alone, that pastes-a-name typo would
            // create an account whose invitation can never be delivered: a 201,
            // a failed send, and a colleague who never hears anything.
            //
            // A 400 naming the field is the better answer, and this is the
            // narrowest rule that gives it. Login is not tightened to match:
            // an address like that can no longer be *created*, and refusing to
            // let an existing account log in would be a different change.
            .Must(email => email is null || !email.Any(char.IsWhiteSpace))
            .WithMessage("'Email' must not contain spaces.");

        RuleFor(c => c.FullName)
            .NotEmpty()
            // Matches Users.FullName's column width.
            .MaximumLength(200);
    }
}
