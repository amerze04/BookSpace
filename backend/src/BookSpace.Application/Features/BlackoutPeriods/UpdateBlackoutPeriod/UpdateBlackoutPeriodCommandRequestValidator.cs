using FluentValidation;

namespace BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;

// Shape only, shared with the create command via BlackoutPeriodFieldRules —
// which is the point of the shared rules: an edit is a full representation, so
// the two payloads have to be judged identically or a value the create endpoint
// refuses could be smuggled in through the edit.
public sealed class UpdateBlackoutPeriodCommandRequestValidator
    : AbstractValidator<UpdateBlackoutPeriodCommandRequest>
{
    public UpdateBlackoutPeriodCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        RuleFor(c => c.BlackoutPeriodId)
            .NotEmpty()
            .WithMessage("BlackoutPeriodId is required.");

        this.AddBlackoutPeriodFieldRules();
    }
}
