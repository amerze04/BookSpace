using FluentValidation;

namespace BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;

// Shape only, shared with the edit command via BlackoutPeriodFieldRules. The
// one rule that needs the clock — a blackout entirely in the past — is
// BlackoutPeriodRules.EnsureNotElapsed in the handler, arriving as 422
// BlackoutPeriodElapsed rather than a field error, because "this interval is
// over" is not a fact about either field on its own.
public sealed class CreateBlackoutPeriodCommandRequestValidator
    : AbstractValidator<CreateBlackoutPeriodCommandRequest>
{
    public CreateBlackoutPeriodCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        this.AddBlackoutPeriodFieldRules();
    }
}
