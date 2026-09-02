using FluentValidation;

namespace BookSpace.Application.Features.BlackoutPeriods.DeleteBlackoutPeriod;

// Shape only, and there are only two fields to judge — both route ids. No
// BlackoutPeriodFieldRules here: a delete carries no interval.
public sealed class DeleteBlackoutPeriodCommandRequestValidator
    : AbstractValidator<DeleteBlackoutPeriodCommandRequest>
{
    public DeleteBlackoutPeriodCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        RuleFor(c => c.BlackoutPeriodId)
            .NotEmpty()
            .WithMessage("BlackoutPeriodId is required.");
    }
}
